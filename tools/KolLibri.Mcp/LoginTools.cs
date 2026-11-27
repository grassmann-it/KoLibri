using System.Text.Json;
using KoLibri;

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
        var accounts = await _accounts.Client.Accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<object>();
        var wanted = McpArgs.Roles(arguments, _options.DefaultRole);
        var explicitRole = arguments.TryGetProperty("role", out _);
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            // Without an explicit role list every account the grant covers; never one it does not.
            if (explicitRole ? !wanted.Contains(role) : !GrantCovers(role)) continue;
            var account = accounts.FirstOrDefault(a => a.Id.Value == McpAccounts.IdOf(role));
            var expired = account?.SessionExpiresAt is { } at && at <= DateTimeOffset.UtcNow;
            rows.Add(new
            {
                role = role.ToString(),
                profileId = account?.Id.Value,
                displayName = account?.PlayerName,
                enabled = account is not null,
                hasCookie = account is not null && (account.HasSession || expired),
                expired,
                expiresAt = account?.SessionExpiresAt,
                endpoint = (string?)null
            });
        }
        return new { command = "login status", accounts = rows };
    }

    private bool GrantCovers(AccountRole role)
    {
        if (Authorizer is null) return true;
        using var probe = JsonDocument.Parse("{\"role\":\"" + role + "\"}");
        return Authorizer("kol_login_status", probe.RootElement.Clone()).Allowed;
    }

    private async Task<object?> LoginClearAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roles = ResolveLoginRoles(OptionalString(arguments, "role"));
        var known = await _accounts.Client.Accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        var cleared = new List<string>();
        foreach (var role in roles)
        {
            var account = known.FirstOrDefault(a => a.Id.Value == McpAccounts.IdOf(role));
            if (account is null) continue;
            await _accounts.Client.Accounts.LogoutAsync(account.Id, cancellationToken).ConfigureAwait(false);
            cleared.Add(role.ToString());
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
        // As in 0.4 this tool (re)binds the role to the login it is given; the HTTP host does not offer it.
        await using var session = await _accounts.Client.LoginAsync(username, password, new LoginOptions
        {
            AccountId = McpAccounts.IdOf(role),
            RememberPassword = true,
            AllowIdentityChange = true
        }, cancellationToken).ConfigureAwait(false);
        return new
        {
            command = "login refresh",
            role = role.ToString(),
            username,
            loggedInAt = DateTimeOffset.UtcNow
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
        => Schema("{\"type\":\"object\",\"properties\":{\"role\":{\"enum\":[\"Main\",\"Mallbot\",\"all\"]}},\"additionalProperties\":false}");

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
