using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using KoLibri.Cli.Repl;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

internal static partial class KolibriCli
{
    private static async Task<int> LoginDispatchAsync(string[] args)
    {
        if (args.Length > 0)
        {
            var head = args[0].ToLowerInvariant();
            if (head is "status" or "--status")
                return await LoginStatusAsync(args[1..]);
            if (head is "clear" or "--clear")
                return await LoginClearAsync(args[1..]);
            if (head is "refresh" or "--refresh")
                return await LoginRefreshAsync(args[1..]);
        }
        return await LoginAsync(args);
    }

    private static async Task<int> LoginAsync(string[] args)
    {
        var roleValue = GetOption(args, "--role")
            ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null)
            ?? throw new ArgumentException("login main oder login mallbot verwenden.");
        var role = ParseRole(roleValue);
        var username = GetOption(args, "--username");
        if (string.IsNullOrWhiteSpace(username) && !Console.IsInputRedirected)
        {
            Console.Error.Write("Benutzername: ");
            username = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Benutzername fehlt.");
        var password = await ReadPasswordAsync();
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Passwort fehlt.");

        var rememberPassword = !HasFlag(args, "--no-remember-password");
        var login = await new KoLLoginClient().LoginAsync(username, password, rememberPassword);
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.SetProfileAsync(new AccountProfile
        {
            Id = role == AccountRole.Main ? "main" : "mallbot",
            DisplayName = username,
            Role = role,
            Enabled = true
        });
        await accounts.SetCredentialsAsync(role, login.Credentials);
        WriteJson(new { ok = true, role = role.ToString(), username, rememberPassword, loggedInAt = login.LoggedInAt });
        return 0;
    }

    /// <summary>
    /// Asks KoL whether the session still works. The answer comes back as
    /// a redirect to the login page when the session was dropped.
    /// </summary>
    private static async Task<bool> SessionStillWorksAsync(AccountCredentials credentials)
    {
        try
        {
            using var http = KoLLoginClient.CreateAuthenticatedClient(
                credentials,
                new Uri("https://www.kingdomofloathing.com/"));
            var response = await http.GetAsync(
                new Uri("https://www.kingdomofloathing.com/api.php?what=status&for=KoLibri"));
            if (!response.IsSuccessStatusCode) return false;
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            if (location.Contains("login.php", StringComparison.OrdinalIgnoreCase)) return false;
            var body = await response.Content.ReadAsStringAsync();
            return body.Contains("playerid", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<int> LoginStatusAsync(string[] args)
    {
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync();
        var profiles = await accounts.GetAccountsAsync();
        var rows = new List<object>();
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            var profile = profiles.FirstOrDefault(p => p.Role == role);
            var session = await accounts.GetSessionAsync(role, includeExpired: true);
            var expired = session?.IsExpired() ?? false;
            var hasCookie = !string.IsNullOrWhiteSpace(session?.Credentials.CookieHeader);
            // The stored expiry only says when the cookie runs out. KoL
            // drops a session earlier — when the password is changed or
            // the account is signed out elsewhere — so the session is
            // asked whether it still works.
            var rejected = false;
            if (hasCookie && !expired)
            {
                rejected = !await SessionStillWorksAsync(session!.Credentials).ConfigureAwait(false);
            }
            rows.Add(new
            {
                role = role.ToString(),
                profileId = profile?.Id,
                displayName = profile?.DisplayName,
                enabled = profile?.Enabled ?? false,
                hasCookie,
                expired,
                rejectedByServer = rejected,
                usable = hasCookie && !expired && !rejected,
                loggedInAt = session?.Credentials.ExpiresAt,
                endpoint = session?.Profile.Endpoint?.ToString()
            });
        }
        WriteJson(new
        {
            command = "login status",
            accounts = rows
        });
        return 0;
    }

    private static async Task<int> LoginClearAsync(string[] args)
    {
        var roleValue = GetOption(args, "--role");
        var roles = ResolveLoginRoles(roleValue);
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync();
        foreach (var role in roles)
        {
            var profile = await accounts.GetProfileAsync(role);
            if (profile is null)
            {
                Console.Error.WriteLine($"login clear: no profile for {role}.");
                continue;
            }
            // Removing credentials by overwriting with an empty password
            // clears the cookie, then we keep the profile so the operator
            // does not have to re-add it on next login.
            try
            {
                await accounts.SetCredentialsAsync(role, new AccountCredentials
                {
                    Password = string.Empty,
                    CookieHeader = string.Empty,
                    ExpiresAt = DateTimeOffset.MinValue
                });
            }
            catch (AccountConfigurationException)
            {
                // No profile configured: silently skip.
            }
            Console.Error.WriteLine($"login clear: cleared credentials for {role} (profile '{profile.Id}' retained).");
        }
        WriteJson(new { command = "login clear", roles = roles.Select(r => r.ToString()).ToArray() });
        return 0;
    }

    private static async Task<int> LoginRefreshAsync(string[] args)
    {
        var roleValue = GetOption(args, "--role") ?? "main";
        var role = ParseRole(roleValue);
        var username = GetOption(args, "--username");
        var password = GetOption(args, "--password");
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
        {
            // No flags: log in again with the password saved for this role. It is never printed.
            var stored = AccountSessionManager.CreateDefault();
            await stored.InitializeAsync();
            var current = await stored.GetSessionAsync(role, includeExpired: true);
            if (current is null)
                throw new ArgumentException($"Kein gespeichertes Konto fuer {role}. Erst 'login {role.ToString().ToLowerInvariant()}' ausfuehren.");
            var renewed = await stored.ReconnectAsync(current,
                (user, pass, token) => new KoLLoginClient().LoginAsync(user, pass, true, token));
            if (renewed is null)
                throw new ArgumentException($"Kein Passwort fuer {role} gespeichert. Erst mit --username/--password anmelden.");
            WriteJson(new { command = "login refresh", role = role.ToString(), ok = true, usedSavedPassword = true });
            return 0;
        }
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            // Fall back to interactive prompt when neither flag was supplied.
            if (!Console.IsInputRedirected)
            {
                if (string.IsNullOrWhiteSpace(username))
                {
                    Console.Error.Write($"login refresh {role} Benutzername: ");
                    username = Console.ReadLine()?.Trim();
                }
                if (string.IsNullOrEmpty(password))
                {
                    password = await ReadPasswordAsync();
                }
            }
        }
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("login refresh braucht --username und --password.");

        var login = await new KoLLoginClient().LoginAsync(username, password, rememberPassword: true);
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync();
        await accounts.SetCredentialsAsync(role, login.Credentials);
        WriteJson(new
        {
            command = "login refresh",
            role = role.ToString(),
            ok = true,
            loggedInAt = login.LoggedInAt
        });
        return 0;
    }

    private static async Task<int> DailyAuditCommandAsync(string[] args)
    {
        // Usage:
        //   kolibri daily-audit [path] [--tail N] [--verify] [--role X]
        var path = args.Length > 0 && !args[0].StartsWith('-')
            ? args[0]
            : KoLibri.Core.Spiel.BridgeAuditLog.DefaultPath();
        var tail = int.TryParse(GetOption(args, "--tail"), out var t) ? t : 0;
        var verify = args.Any(a => string.Equals(a, "--verify", StringComparison.OrdinalIgnoreCase));
        var roleFilter = GetOption(args, "--role");
        var accountFilter = GetOption(args, "--account");

        await using var log = new KoLibri.Core.Spiel.BridgeAuditLog(path);
        var rows = log.ReadSealedEvents();
        if (verify)
        {
            var breakInfo = log.VerifyChain();
            if (breakInfo is null)
            {
                WriteJson(new { command = "daily-audit", path, state = "verified", rowCount = rows.Count });
                return 0;
            }
            WriteJson(new
            {
                command = "daily-audit",
                path,
                state = "broken",
                breakIndex = breakInfo.Index,
                brokenAction = breakInfo.BrokenRow.AuditEvent.Action,
                expectedHash = breakInfo.ExpectedHash,
                actualHash = breakInfo.BrokenRow.Hash
            });
            return 1;
        }
        IEnumerable<KoLibri.Core.Basis.SealedAuditEvent> filtered = rows;
        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            // legacy `role` filter maps Main/Mallbot to accountId values; we
            // treat either as a substring match so the operator can grep.
            filtered = filtered.Where(r =>
                r.AuditEvent.AccountId?.Contains(roleFilter, StringComparison.OrdinalIgnoreCase) == true);
        }
        if (!string.IsNullOrWhiteSpace(accountFilter))
        {
            filtered = filtered.Where(r =>
                r.AuditEvent.AccountId?.Contains(accountFilter, StringComparison.OrdinalIgnoreCase) == true);
        }
        var ordered = filtered.ToArray();
        if (tail > 0) ordered = ordered.TakeLast(tail).ToArray();
        WriteJson(new
        {
            command = "daily-audit",
            path,
            rowCount = ordered.Length,
            rows = ordered.Select(r => new
            {
                occurredAt = r.AuditEvent.OccurredAt,
                phase = r.AuditEvent.Phase.ToString(),
                accountId = r.AuditEvent.AccountId,
                action = r.AuditEvent.Action,
                status = r.AuditEvent.Status,
                requestId = r.AuditEvent.RequestId,
                message = r.AuditEvent.Message,
                hash = r.Hash
            })
        });
        return 0;
    }

    private static async Task<int> TokenDispatchAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
        var store = new KoLibri.Core.Basis.ApprovalTokenStore(
            KoLibri.Core.Basis.ApprovalTokenStore.DefaultRoot());
        switch (sub)
        {
            case "mint":
            case "new":
            {
                var label = GetOption(args[1..], "--label");
                var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var record = new KoLibri.Core.Basis.ApprovalToken(
                    Token: token,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Label: label ?? "kolibri-cli");
                await store.SaveAsync(record);
                WriteJson(new
                {
                    command = "token mint",
                    path = store.FilePath,
                    token,
                    record.CreatedAt,
                    record.Label
                });
                return 0;
            }
            case "show":
            {
                var token = await store.LoadAsync();
                if (token is null)
                {
                    WriteJson(new { command = "token show", state = "missing", path = store.FilePath });
                    return 1;
                }
                WriteJson(new
                {
                    command = "token show",
                    state = "present",
                    path = store.FilePath,
                    token = token.Token,
                    token.CreatedAt,
                    token.Label
                });
                return 0;
            }
            default:
                Console.Error.WriteLine("token: subcommand muss mint oder show sein.");
                return 2;
        }
    }

    private static List<AccountRole> ResolveLoginRoles(string? roleValue)
    {
        var value = (roleValue ?? "all").Trim().ToLowerInvariant();
        if (value is "all" or "both")
            return new() { AccountRole.Main, AccountRole.Mallbot };
        return new() { ParseRole(value) };
    }
}
