using System.Text.Json;
using CoreBasis = KoLibri.Core.Basis;

namespace KoLibri.Internal;

/// <summary>
/// Default <see cref="IAccountStore"/> in 0.5: the account files of 0.4 (accounts.json plus the encrypted
/// secret store), so CLI, MCP and Desktop keep their stored sessions. 0.4 knew two roles only, so the ids are
/// fixed: <c>main</c> and <c>mallbot</c>. Labels and tags live in a small side file.
/// Free account ids need a custom <see cref="KolibriOptions.AccountStore"/> until the store migration lands.
/// </summary>
internal sealed class RoleAccountStore : IAccountStore
{
    private readonly CoreBasis.AccountSessionManager _manager;
    private readonly string _labelsPath;
    private readonly object _labelsGate = new();
    private readonly Dictionary<string, string?> _userAgents = new(StringComparer.OrdinalIgnoreCase);

    public RoleAccountStore(string? stateDirectory)
    {
        if (stateDirectory is null)
        {
            _manager = CoreBasis.AccountSessionManager.CreateDefault();
            _labelsPath = Path.Combine(Path.GetDirectoryName(CoreBasis.JsonFileAccountStore.DefaultPath())!, "kolibri-account-labels.json");
        }
        else
        {
            var root = Path.GetFullPath(stateDirectory);
            _manager = new CoreBasis.AccountSessionManager(
                new CoreBasis.JsonFileAccountStore(Path.Combine(root, "accounts.json")),
                new CoreBasis.FileSecretStore(Path.Combine(root, "secrets")));
            _labelsPath = Path.Combine(root, "account-labels.json");
        }
    }

    public static string IdOf(CoreBasis.AccountRole role) => role == CoreBasis.AccountRole.Main ? "main" : "mallbot";

    public static CoreBasis.AccountRole RoleOf(AccountId id) => id.Value.Trim().ToLowerInvariant() switch
    {
        "main" => CoreBasis.AccountRole.Main,
        "mallbot" => CoreBasis.AccountRole.Mallbot,
        _ => throw new KolibriException(
            $"The default account store of 0.5 knows the ids 'main' and 'mallbot' only (not '{id}'). " +
            "Pass LoginOptions.AccountId = \"main\" or \"mallbot\", or set KolibriOptions.AccountStore.")
    };

    public string? UserAgentFor(AccountId id)
    {
        lock (_userAgents) return _userAgents.TryGetValue(id.Value, out var agent) ? agent : null;
    }

    public async Task<IReadOnlyList<AccountRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        await _manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var labels = ReadLabels();
        var result = new List<AccountRecord>();
        foreach (var role in Enum.GetValues<CoreBasis.AccountRole>())
        {
            var profile = await _manager.GetProfileAsync(role, cancellationToken).ConfigureAwait(false);
            if (profile is null) continue;
            var session = await _manager.GetSessionAsync(role, includeExpired: true, cancellationToken).ConfigureAwait(false);
            var id = IdOf(role);
            labels.TryGetValue(id, out var label);
            lock (_userAgents) _userAgents[id] = session?.Credentials.UserAgent;
            var account = new Account(
                new AccountId(id),
                profile.DisplayName,
                int.TryParse(profile.PlayerId, out var playerId) ? playerId : null,
                label?.Label,
                label?.Tags ?? new[] { id });
            result.Add(new AccountRecord(account, session?.Credentials.CookieHeader, session?.Credentials.Password, session?.Credentials.ExpiresAt));
        }
        return result;
    }

    public async Task SaveAsync(AccountRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var role = RoleOf(record.Account.Id);
        await _manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var current = await _manager.GetProfileAsync(role, cancellationToken).ConfigureAwait(false);
        var playerId = record.Account.PlayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (current is null || current.DisplayName != record.Account.PlayerName || current.PlayerId != (playerId ?? current.PlayerId))
        {
            await _manager.SetProfileAsync(new CoreBasis.AccountProfile
            {
                Id = current?.Id ?? IdOf(role),
                DisplayName = record.Account.PlayerName,
                Role = role,
                PlayerId = playerId ?? current?.PlayerId,
                Endpoint = current?.Endpoint,
                Enabled = true
            }, cancellationToken).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(record.CookieHeader) && string.IsNullOrEmpty(record.Password))
        {
            await _manager.ClearCredentialsAsync(role, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var previous = await _manager.GetSessionAsync(role, includeExpired: true, cancellationToken).ConfigureAwait(false);
            await _manager.SetCredentialsAsync(role, new CoreBasis.AccountCredentials
            {
                CookieHeader = record.CookieHeader,
                AccessToken = previous?.Credentials.AccessToken ?? SessionToken(record.CookieHeader),
                Username = record.Account.PlayerName,
                Password = record.Password,
                UserAgent = previous?.Credentials.UserAgent ?? CoreBasis.KoLUserAgent.Value,
                ExpiresAt = record.ExpiresAt
            }, cancellationToken).ConfigureAwait(false);
        }
        WriteLabel(IdOf(role), record.Account.Label, record.Account.Tags);
    }

    public async Task DeleteAsync(AccountId id, CancellationToken cancellationToken)
    {
        var role = RoleOf(id);
        await _manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        // 0.4's store cannot drop a role profile; removing forgets the secret, which ends every session.
        await _manager.ClearCredentialsAsync(role, cancellationToken).ConfigureAwait(false);
        WriteLabel(IdOf(role), null, null);
    }

    /// <summary>0.4 kept the session cookie value as access token; the secret store requires one of token or password.</summary>
    private static string? SessionToken(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return null;
        var pairs = cookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToArray();
        return pairs.FirstOrDefault(p => p[0].Equals("charpwd", StringComparison.OrdinalIgnoreCase))?[1]
               ?? pairs.FirstOrDefault(p => p[0].Equals("PHPSESSID", StringComparison.OrdinalIgnoreCase))?[1]
               ?? cookieHeader;
    }

    private sealed record LabelEntry(string? Label, string[]? Tags);

    private Dictionary<string, LabelEntry> ReadLabels()
    {
        lock (_labelsGate)
        {
            try
            {
                if (!File.Exists(_labelsPath)) return new(StringComparer.OrdinalIgnoreCase);
                return JsonSerializer.Deserialize<Dictionary<string, LabelEntry>>(File.ReadAllText(_labelsPath))
                       is { } map ? new(map, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private void WriteLabel(string id, string? label, IReadOnlyList<string>? tags)
    {
        lock (_labelsGate)
        {
            var map = ReadLabels();
            var defaultTags = tags is null || (tags.Count == 1 && tags[0] == id) || tags.Count == 0;
            if (label is null && defaultTags) map.Remove(id);
            else map[id] = new LabelEntry(label, tags?.ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(_labelsPath)!);
            var temp = _labelsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map));
            File.Move(temp, _labelsPath, overwrite: true);
        }
    }
}
