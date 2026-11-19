using System.Globalization;
using System.Text.Json;
using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// Lightweight append-only audit journal for bridge-driven daily runs.
/// Writes <see cref="SealedAuditEvent"/> rows to a JSONL file in
/// <c>~/.local/share/KoLibri/bridge-audit.jsonl</c> (overridable via the
/// <see cref="BridgeAuditLog"/> constructor). Each row carries a SHA-256
/// hash of the row before it; <see cref="VerifyChain"/> walks the chain
/// and returns the first break, or <c>null</c> when the chain is intact.
///
/// The journal deliberately lives in the daily namespace instead of the
/// integration layer because it is owned by the bridge flow, not the
/// generic KoL HTTP layer.
/// </summary>
public sealed class BridgeAuditLog : IDisposable, IAsyncDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _previousHash;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public BridgeAuditLog(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Audit path is required.", nameof(path));
        _path = path;
        // Resume from the previous chain when the file already exists, so
        // a follow-up run continues the hash chain instead of starting
        // over with a null prevHash.
        if (File.Exists(_path))
        {
            var rows = ReadSealedEvents();
            if (rows.Count > 0) _previousHash = rows[^1].Hash;
        }
    }

    public string FilePath => _path;

    public static string DefaultPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoLibri");
        return Path.Combine(root, "bridge-audit.jsonl");
    }

    public async Task<SealedAuditEvent> AppendAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ThrowIfDisposed();
        var prev = _previousHash;
        var hash = AuditHashChain.ComputeHash(prev, auditEvent);
        var sealedEvent = new SealedAuditEvent(auditEvent, prev, hash);
        var line = JsonSerializer.Serialize(sealedEvent, JsonOptions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        _previousHash = hash;
        return sealedEvent;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<SealedAuditEvent> ReadSealedEvents()
    {
        ThrowIfDisposed();
        if (!File.Exists(_path)) return Array.Empty<SealedAuditEvent>();
        var rows = new List<SealedAuditEvent>();
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var row = JsonSerializer.Deserialize<SealedAuditEvent>(line, JsonOptions);
                if (row is not null) rows.Add(row);
            }
            catch (JsonException)
            {
                // skip unparseable rows; verification focuses on chain integrity,
                // not malformed rows.
            }
        }
        return rows;
    }

    public AuditChainBreak? VerifyChain()
    {
        return AuditHashChain.Verify(ReadSealedEvents());
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BridgeAuditLog));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
