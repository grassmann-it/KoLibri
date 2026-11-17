using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Integration;

/// <summary>Persistence options for an append-only audit JSONL file.</summary>
public sealed record AuditJournalOptions
{
    /// <summary>
    /// If true, malformed rows are skipped while reading.  The default is
    /// strict so corruption cannot silently hide an audit entry.
    /// </summary>
    public bool IgnoreMalformedLines { get; init; }

    /// <summary>Flush each completed row through the OS write cache.</summary>
    public bool FlushToDisk { get; init; } = true;

    /// <summary>Maximum bytes accepted for one JSONL row.</summary>
    public int MaxEventBytes { get; init; } = 1_048_576;

    public void Validate()
    {
        if (MaxEventBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEventBytes));
    }
}

/// <summary>Raised when a persisted audit row is not valid JSON or an event.</summary>
public sealed class AuditJournalFormatException : FormatException
{
    public AuditJournalFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Append/read/query boundary for the local audit journal.  Implementations
/// never contact KoL and never perform the business mutation being recorded.
/// </summary>
public interface IAuditEventJournal : IAsyncDisposable, IDisposable
{
    string Path { get; }

    Task<AuditEvent> AppendAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEvent>> ReadAsync(
        AuditEventQuery? query = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AuditEvent> ReadAllAsync(
        AuditEventQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEvent>> QueryAsync(
        AuditEventQuery query,
        CancellationToken cancellationToken = default);

    Task<AuditEvent> AppendPreviewAsync(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "created",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false);

    Task<AuditEvent> AppendCommitAsync(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "submitted",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false);

    Task<AuditEvent> AppendVerifyAsync(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "verified",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false);
}

/// <summary>Stable contract name used by the application-level API map.</summary>
public interface IAuditLog : IAuditEventJournal
{
}

/// <summary>
/// Durable JSON Lines audit journal.  Every append serializes one complete
/// row in memory, takes a per-path gate, writes it in one append operation,
/// and flushes it before returning.  A failed write is rolled back to the
/// original file length so callers never observe a knowingly partial row.
/// </summary>
public sealed class JsonlAuditEventJournal : IAuditLog
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly AuditJournalOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate;
    private bool _disposed;

    public JsonlAuditEventJournal(string path, AuditJournalOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A journal path is required.", nameof(path));

        _path = System.IO.Path.GetFullPath(path);
        _options = options ?? new AuditJournalOptions();
        _options.Validate();
        _jsonOptions = CreateJsonOptions();
        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public string Path => _path;

    /// <summary>Gets the conventional per-user local audit path.</summary>
    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = string.IsNullOrWhiteSpace(appData)
            ? System.IO.Path.Combine(Environment.CurrentDirectory, ".kolarbitrage")
            : System.IO.Path.Combine(appData, "KolArbitrage");
        return System.IO.Path.Combine(root, "audit.jsonl");
    }

    public async Task<AuditEvent> AppendAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(auditEvent);
        auditEvent.ValidateForAppend();

        // Serialize before taking the gate.  This avoids holding the file
        // lock while JSON conversion runs and gives us one immutable byte
        // buffer to append atomically.
        var line = SerializeLine(auditEvent);
        if (line.Length > _options.MaxEventBytes)
            throw new ArgumentException(
                $"The serialized audit event exceeds {_options.MaxEventBytes} bytes.",
                nameof(auditEvent));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            // FileShare.Read keeps readers available while preventing another
            // process from opening a competing writer.  Same-process writers
            // share the gate above, including separate journal instances.
            await using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            var originalLength = stream.Length;
            try
            {
                // Once append starts we intentionally do not cancel the write:
                // cancellation halfway through a JSON object would defeat the
                // journal's append-only/atomic-line contract.
                await stream.WriteAsync(line, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (_options.FlushToDisk)
                    stream.Flush(flushToDisk: true);
            }
            catch
            {
                // Best effort rollback for disk-full and I/O failures.  The
                // stream still owns the same append handle, so no other writer
                // can race this truncation while the gate is held.
                try
                {
                    stream.SetLength(originalLength);
                    stream.Flush(flushToDisk: _options.FlushToDisk);
                }
                catch
                {
                    // Preserve the original write exception.  A later read
                    // will report an invalid row if the filesystem refused
                    // the rollback too.
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        return auditEvent;
    }

    public Task<IReadOnlyList<AuditEvent>> ReadAsync(
        AuditEventQuery? query = null,
        CancellationToken cancellationToken = default)
        => ReadCoreAsync(query, cancellationToken);

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(
        AuditEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadCoreAsync(query, cancellationToken);
    }

    public async IAsyncEnumerable<AuditEvent> ReadAllAsync(
        AuditEventQuery? query = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var events = await ReadCoreAsync(query, cancellationToken).ConfigureAwait(false);
        foreach (var auditEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return auditEvent;
        }
    }

    public Task<AuditEvent> AppendPreviewAsync(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "created",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false)
        => AppendAsync(
            AuditEvent.Preview(operationId, sourceId, requestId, action, status, message, data, occurredAt,
                accountId: accountId, isStale: isStale),
            cancellationToken);

    public Task<AuditEvent> AppendCommitAsync(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "submitted",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false)
        => AppendAsync(
            AuditEvent.Commit(operationId, sourceId, requestId, action, status, message, data, occurredAt,
                accountId: accountId, isStale: isStale),
            cancellationToken);

    public Task<AuditEvent> AppendVerifyAsync(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "verified",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false)
        => AppendAsync(
            AuditEvent.Verify(operationId, sourceId, requestId, action, status, message, data, occurredAt,
                accountId: accountId, isStale: isStale),
            cancellationToken);

    public void Dispose()
    {
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<IReadOnlyList<AuditEvent>> ReadCoreAsync(
        AuditEventQuery? query,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        query?.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<AuditEvent>();

            var result = new List<AuditEvent>();
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lineNumber = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                AuditEvent? auditEvent;
                try
                {
                    auditEvent = JsonSerializer.Deserialize<AuditEvent>(line, _jsonOptions);
                    if (auditEvent is null)
                        throw new JsonException("The JSON value was null.");
                    auditEvent.ValidateForAppend();
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
                {
                    if (_options.IgnoreMalformedLines)
                        continue;
                    throw new AuditJournalFormatException(
                        $"Audit journal '{_path}' contains an invalid row at line {lineNumber}.",
                        exception);
                }

                if (query is null || Matches(auditEvent, query))
                    result.Add(auditEvent);
            }

            IEnumerable<AuditEvent> ordered = query?.Descending == true
                ? result.AsEnumerable().Reverse()
                : result;
            if (query?.Limit is int limit)
                ordered = ordered.Take(limit);
            return ordered.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Matches(AuditEvent auditEvent, AuditEventQuery query)
    {
        if (query.OperationId is not null && !string.Equals(auditEvent.OperationId, query.OperationId, StringComparison.Ordinal))
            return false;
        if (query.AccountId is not null && !string.Equals(auditEvent.AccountId, query.AccountId, StringComparison.Ordinal))
            return false;
        if (query.SourceId is not null && !string.Equals(auditEvent.SourceId, query.SourceId, StringComparison.Ordinal))
            return false;
        if (query.RequestId is not null && !string.Equals(auditEvent.RequestId, query.RequestId, StringComparison.Ordinal))
            return false;
        if (query.Action is not null && !string.Equals(auditEvent.Action, query.Action, StringComparison.Ordinal))
            return false;
        if (query.Phase is not null && auditEvent.Phase != query.Phase.Value)
            return false;
        if (query.Phases is not null && !query.Phases.Contains(auditEvent.Phase))
            return false;
        var from = query.From ?? query.Since;
        var to = query.To ?? query.Until;
        if (from is not null && auditEvent.OccurredAt < from.Value)
            return false;
        if (to is not null && auditEvent.OccurredAt > to.Value)
            return false;
        if (query.IsStale is not null && auditEvent.IsStale != query.IsStale.Value)
            return false;
        return true;
    }

    private byte[] SerializeLine(AuditEvent auditEvent)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(auditEvent, _jsonOptions);
        var line = new byte[json.Length + 1];
        Buffer.BlockCopy(json, 0, line, 0, json.Length);
        line[^1] = (byte)'\n';
        return line;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(JsonlAuditEventJournal));
    }
}

/// <summary>Short alias for hosts that call a JSONL journal an audit journal.</summary>
public sealed class JsonlAuditJournal : IAuditEventJournal
{
    private readonly JsonlAuditEventJournal _inner;

    public JsonlAuditJournal(string path, AuditJournalOptions? options = null)
        => _inner = new JsonlAuditEventJournal(path, options);

    public string Path => _inner.Path;
    public Task<AuditEvent> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => _inner.AppendAsync(auditEvent, cancellationToken);
    public Task<IReadOnlyList<AuditEvent>> ReadAsync(AuditEventQuery? query = null, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(query, cancellationToken);
    public IAsyncEnumerable<AuditEvent> ReadAllAsync(AuditEventQuery? query = null, CancellationToken cancellationToken = default)
        => _inner.ReadAllAsync(query, cancellationToken);
    public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditEventQuery query, CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
    public Task<AuditEvent> AppendPreviewAsync(string operationId, string sourceId, string requestId, string action, string? status = "created", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendPreviewAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public Task<AuditEvent> AppendCommitAsync(string operationId, string sourceId, string requestId, string action, string? status = "submitted", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendCommitAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public Task<AuditEvent> AppendVerifyAsync(string operationId, string sourceId, string requestId, string action, string? status = "verified", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendVerifyAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public void Dispose() => _inner.Dispose();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>Compatibility alias for callers that use a non-extension JSON name.</summary>
public sealed class JsonAuditEventJournal : IAuditEventJournal
{
    private readonly JsonlAuditEventJournal _inner;

    public JsonAuditEventJournal(string path, AuditJournalOptions? options = null)
        => _inner = new JsonlAuditEventJournal(path, options);

    public string Path => _inner.Path;
    public Task<AuditEvent> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => _inner.AppendAsync(auditEvent, cancellationToken);
    public Task<IReadOnlyList<AuditEvent>> ReadAsync(AuditEventQuery? query = null, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(query, cancellationToken);
    public IAsyncEnumerable<AuditEvent> ReadAllAsync(AuditEventQuery? query = null, CancellationToken cancellationToken = default)
        => _inner.ReadAllAsync(query, cancellationToken);
    public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditEventQuery query, CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
    public Task<AuditEvent> AppendPreviewAsync(string operationId, string sourceId, string requestId, string action, string? status = "created", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendPreviewAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public Task<AuditEvent> AppendCommitAsync(string operationId, string sourceId, string requestId, string action, string? status = "submitted", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendCommitAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public Task<AuditEvent> AppendVerifyAsync(string operationId, string sourceId, string requestId, string action, string? status = "verified", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendVerifyAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public void Dispose() => _inner.Dispose();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// Default-name compatibility wrapper for hosts that use <c>AuditEventJournal</c>
/// rather than the concrete JSONL storage name.
/// </summary>
public sealed class AuditEventJournal : IAuditLog
{
    private readonly JsonlAuditEventJournal _inner;

    public AuditEventJournal(string? path = null, AuditJournalOptions? options = null)
        => _inner = new JsonlAuditEventJournal(path ?? JsonlAuditEventJournal.DefaultPath(), options);

    public string Path => _inner.Path;
    public Task<AuditEvent> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => _inner.AppendAsync(auditEvent, cancellationToken);
    public Task<IReadOnlyList<AuditEvent>> ReadAsync(AuditEventQuery? query = null, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(query, cancellationToken);
    public IAsyncEnumerable<AuditEvent> ReadAllAsync(AuditEventQuery? query = null, CancellationToken cancellationToken = default)
        => _inner.ReadAllAsync(query, cancellationToken);
    public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditEventQuery query, CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
    public Task<AuditEvent> AppendPreviewAsync(string operationId, string sourceId, string requestId, string action, string? status = "created", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendPreviewAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public Task<AuditEvent> AppendCommitAsync(string operationId, string sourceId, string requestId, string action, string? status = "submitted", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendCommitAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public Task<AuditEvent> AppendVerifyAsync(string operationId, string sourceId, string requestId, string action, string? status = "verified", string? message = null, IReadOnlyDictionary<string, string?>? data = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default, string? accountId = null, bool isStale = false)
        => _inner.AppendVerifyAsync(operationId, sourceId, requestId, action, status, message, data, occurredAt, cancellationToken, accountId, isStale);
    public void Dispose() => _inner.Dispose();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
