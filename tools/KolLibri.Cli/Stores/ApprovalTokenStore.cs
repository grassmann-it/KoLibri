// CLI copy of the 0.4 store from KoLibri.Core (0.5 step 3): same file format, so tokens and settings stay valid.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoLibri.Cli;

/// <summary>
/// Small JSON file that stores a single approval token. The file lives inside a
/// host-chosen root directory; callers must go through <see cref="ApprovalTokenStore"/>
/// so path traversal and accidental overwrites are blocked.
/// </summary>
internal sealed record ApprovalToken(string Token, DateTimeOffset CreatedAt, string? Label = null);

/// <summary>
/// File-backed, single-token store with a mandatory allowlisted root directory.
/// Path resolution collapses traversal sequences, rejects absolute paths, and
/// refuses symbolic links to prevent a host from being tricked into reading
/// or overwriting a file outside its own scope.
/// </summary>
internal sealed class ApprovalTokenStore : IDisposable
{
    private readonly string _root;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json;
    private bool _disposed;

    public ApprovalTokenStore(string root, string fileName = "approval-token.json")
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A root directory is required.", nameof(root));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.", nameof(fileName));

        var resolvedRoot = ResolveDirectory(root);
        _root = resolvedRoot;
        _path = ResolveInsideRoot(resolvedRoot, fileName);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>Absolute path to the token file inside the allowlisted root.</summary>
    public string FilePath => _path;

    /// <summary>Absolute path to the allowlisted root directory.</summary>
    public string Root => _root;

    public static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Environment.CurrentDirectory, ".kolibri")
            : Path.Combine(localAppData, "KoLibri");
        return root;
    }

    public async ValueTask<ApprovalToken?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous);
            var value = await JsonSerializer.DeserializeAsync<ApprovalToken>(
                stream, _json, cancellationToken).ConfigureAwait(false);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(ApprovalToken token, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(token);
        if (string.IsNullOrWhiteSpace(token.Token))
            throw new ArgumentException("The approval token must not be empty.", nameof(token));

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tmp = _path + ".tmp";
            await using (var stream = new FileStream(
                tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, token, _json, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryValidatePath(string candidate, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (ContainsTraversalSegment(candidate)) return false;
        string resolved;
        try { resolved = ResolveInsideRoot(_root, candidate); }
        catch (ArgumentException) { return false; }
        fullPath = resolved;
        return true;
    }

    private static bool ContainsTraversalSegment(string candidate)
    {
        if (candidate.IndexOf('\u0000') >= 0) return true;
        var separators = new[] { '/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = candidate.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
            if (string.Equals(segment, "..", StringComparison.Ordinal))
                return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private static string ResolveDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            Directory.CreateDirectory(full);
        // Reject symlink pointing outside the original intent by canonicalising
        // and verifying the resulting directory exists as a real directory.
        var info = new DirectoryInfo(full);
        if (info.LinkTarget is not null)
            throw new ArgumentException(
                $"The path '{path}' resolves to a symbolic link; refusing to follow it.",
                nameof(path));
        return info.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveInsideRoot(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.", nameof(fileName));
        if (fileName.IndexOf('\u0000') >= 0)
            throw new ArgumentException("The file name contains an invalid NUL character.", nameof(fileName));

        var combined = Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(root, fileName);
        var full = Path.GetFullPath(combined);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFull = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedFull.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(normalizedFull, normalizedRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The path '{fileName}' escapes the configured root '{root}'.",
                nameof(fileName));
        }
        if (Path.GetFileName(fileName).IndexOfAny(new[] { '\u0000' }) >= 0)
            throw new ArgumentException("The file name contains an invalid NUL character.", nameof(fileName));
        return normalizedFull;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ApprovalTokenStore));
    }
}
