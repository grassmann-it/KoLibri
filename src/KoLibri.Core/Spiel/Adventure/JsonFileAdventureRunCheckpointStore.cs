using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Local, secret-free checkpoint store for crash-resume. Checkpoints are keyed
/// by a SHA-256 filename so run ids can never escape the configured directory.
/// Writes use a temporary file followed by an atomic replace where supported.
/// </summary>
public sealed class JsonFileAdventureRunCheckpointStore : IAdventureExecutionCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileAdventureRunCheckpointStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A checkpoint directory is required.", nameof(directory));
        _directory = Path.GetFullPath(directory.Trim());
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public async Task SaveAsync(AdventureCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var value = checkpoint.Normalize();
        if (string.IsNullOrWhiteSpace(value.RunId))
            throw new ArgumentException("Checkpoint run id is required.", nameof(checkpoint));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnlockedAsync(value, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<AdventureCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadUnlockedAsync(runId.Trim(), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<AdventureCheckpointCasResult> CompareAndSwapAsync(
        string runId,
        AdventureCheckpoint? expected,
        AdventureCheckpoint replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(replacement);
        var normalizedId = runId.Trim();
        var value = replacement.Normalize();
        if (!string.Equals(value.RunId, normalizedId, StringComparison.Ordinal))
            throw new ArgumentException("Replacement checkpoint run id does not match the requested run id.", nameof(replacement));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(normalizedId, cancellationToken).ConfigureAwait(false);
            if (!AdventureCheckpointComparer.Equals(current, expected))
                return AdventureCheckpointCasResult.Failed(current);
            await WriteUnlockedAsync(value, cancellationToken).ConfigureAwait(false);
            return AdventureCheckpointCasResult.Success(value, current);
        }
        finally { _gate.Release(); }
    }

    private async Task<AdventureCheckpoint?> ReadUnlockedAsync(string runId, CancellationToken cancellationToken)
    {
        var path = PathFor(runId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return (await JsonSerializer.DeserializeAsync<AdventureCheckpoint>(stream, JsonOptions, cancellationToken).ConfigureAwait(false))?.Normalize();
    }

    private async Task WriteUnlockedAsync(AdventureCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(checkpoint.RunId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 16 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string PathFor(string runId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        return Path.Combine(_directory, digest + ".json");
    }
}
