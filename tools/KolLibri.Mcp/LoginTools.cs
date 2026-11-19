using System.Text.Json;
using KolArbitrage.Core.Accounts;

namespace KolLibri.Mcp;

/// <summary>
/// Login sub-commands surfaced through MCP. Mirrors the CLI's
/// `login status | clear | refresh` sub-command set so an MCP host can
/// drive the same flows without depending on a shell.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterLoginTools()
    {
        Register("kol_login_status",
            "List stored account sessions for Main and Mallbot (profile id, cookie presence, expiry, endpoint).",
            LoginStatusSchema(),
            LoginStatusAsync);
        Register("kol_login_clear",
            "Clear stored credentials for one or both roles. Profiles are retained.",
            LoginClearSchema(),
            LoginClearAsync);
        Register("kol_login_refresh",
            "Force a fresh KoL login for one role and store the resulting credentials.",
            LoginRefreshSchema(),
            LoginRefreshAsync);

        Alias("login-status", "kol_login_status");
        Alias("login_clear", "kol_login_clear");
        Alias("login-refresh", "kol_login_refresh");
    }

    private async Task<object?> LoginStatusAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = AccountSessionManager.CreateDefault();
        await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var profiles = await manager.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<object>();
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            var profile = profiles.FirstOrDefault(p => p.Role == role);
            var session = await manager.GetSessionAsync(role, includeExpired: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            rows.Add(new
            {
                role = role.ToString(),
                profileId = profile?.Id,
                displayName = profile?.DisplayName,
                enabled = profile?.Enabled ?? false,
                hasCookie = !string.IsNullOrWhiteSpace(session?.Credentials.CookieHeader),
                expired = session?.IsExpired() ?? false,
                expiresAt = session?.Credentials.ExpiresAt,
                endpoint = session?.Profile.Endpoint?.ToString()
            });
        }
        return new { command = "login status", accounts = rows };
    }

    private async Task<object?> LoginClearAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roleValue = OptionalString(arguments, "role");
        var roles = ResolveLoginRoles(roleValue);
        var manager = AccountSessionManager.CreateDefault();
        await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var cleared = new List<string>();
        foreach (var role in roles)
        {
            var profile = await manager.GetProfileAsync(role, cancellationToken).ConfigureAwait(false);
            if (profile is null) continue;
            try
            {
                await manager.SetCredentialsAsync(role, new AccountCredentials
                {
                    Password = string.Empty,
                    CookieHeader = string.Empty,
                    ExpiresAt = DateTimeOffset.MinValue
                }, cancellationToken).ConfigureAwait(false);
                cleared.Add(role.ToString());
            }
            catch (AccountConfigurationException) { }
        }
        return new { command = "login clear", cleared };
    }

    private async Task<object?> LoginRefreshAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roleText = RequiredString(arguments, "role");
        if (!Enum.TryParse<AccountRole>(roleText, true, out var role))
            throw new McpInvalidParamsException($"role must be 'Main' or 'Mallbot' (got '{roleText}').");
        var username = RequiredString(arguments, "username");
        var password = RequiredString(arguments, "password");
        var login = await new KoLLoginClient().LoginAsync(username, password, rememberPassword: true, cancellationToken).ConfigureAwait(false);
        var manager = AccountSessionManager.CreateDefault();
        await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await manager.SetCredentialsAsync(role, login.Credentials, cancellationToken).ConfigureAwait(false);
        return new
        {
            command = "login refresh",
            role = role.ToString(),
            username,
            loggedInAt = login.LoggedInAt
        };
    }

    private static List<AccountRole> ResolveLoginRoles(string? roleValue)
    {
        var value = (roleValue ?? "all").Trim().ToLowerInvariant();
        if (value is "all" or "both") return new() { AccountRole.Main, AccountRole.Mallbot };
        return Enum.TryParse<AccountRole>(value, true, out var role)
            ? new() { role }
            : new();
    }

    private static JsonElement LoginStatusSchema()
        => Schema("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");

    private static JsonElement LoginClearSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\",\"all\"]}"
            + "},\"additionalProperties\":false}");

    private static JsonElement LoginRefreshSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},"
            + "\"username\":{\"type\":\"string\"},"
            + "\"password\":{\"type\":\"string\"}"
            + "},\"required\":[\"role\",\"username\",\"password\"],\"additionalProperties\":false}");
}
