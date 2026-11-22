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
            ?? throw new ArgumentException("Use login main or login mallbot.");
        var role = ParseRole(roleValue);
        var username = GetOption(args, "--username");
        if (string.IsNullOrWhiteSpace(username) && !Console.IsInputRedirected)
        {
            Console.Error.Write("Username: ");
            username = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username is missing.");
        var password = await ReadPasswordAsync();
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is missing.");

        var rememberPassword = !HasFlag(args, "--no-remember-password");
        var login = await new KoLLoginClient().LoginAsync(username, password, rememberPassword);
        var accounts = AccountSessionManager.CreateDefault();
        var verified = await ReadLoginIdentityAsync(login.Credentials);
        if (verified is not null)
            await accounts.ApplyVerifiedLoginAsync(role, verified, login.Credentials, allowAccountChange: true);
        else
        {
            Console.Error.WriteLine("login: could not verify the player id; saving without it.");
            await accounts.SetProfileAsync(new AccountProfile
            {
                Id = role == AccountRole.Main ? "main" : "mallbot",
                DisplayName = username,
                Role = role,
                Enabled = true
            });
            await accounts.SetCredentialsAsync(role, login.Credentials);
        }
        WriteJson(new { ok = true, role = role.ToString(), username, rememberPassword, loggedInAt = login.LoggedInAt });
        return 0;
    }

    internal enum SessionProbe { Works, Rejected, Unreachable }

    /// <summary>
    /// Asks KoL whether the session still works. Only a redirect to the login page, a 401/403 or a
    /// status answer without a player id counts as a rejection; timeouts, network errors and other
    /// HTTP errors (e.g. 503) leave the question open.
    /// </summary>
    internal static async Task<SessionProbe> ProbeSessionAsync(AccountCredentials credentials, HttpMessageHandler? handler = null)
    {
        try
        {
            using var http = handler is null
                ? KoLLoginClient.CreateAuthenticatedClient(credentials, new Uri("https://www.kingdomofloathing.com/"))
                : new HttpClient(handler);
            using var response = await http.GetAsync(
                new Uri("https://www.kingdomofloathing.com/api.php?what=status&for=KoLibri")).ConfigureAwait(false);
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            if (location.Contains("login.php", StringComparison.OrdinalIgnoreCase)) return SessionProbe.Rejected;
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return SessionProbe.Rejected;
            if (!response.IsSuccessStatusCode) return SessionProbe.Unreachable;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return body.Contains("playerid", StringComparison.OrdinalIgnoreCase) ? SessionProbe.Works : SessionProbe.Rejected;
        }
        catch (HttpRequestException) { return SessionProbe.Unreachable; }
        catch (TaskCanceledException) { return SessionProbe.Unreachable; }
    }

    internal static object LoginStatusRow(AccountRole role, AccountProfile? profile, AccountSession? session, SessionProbe? probe)
    {
        var expired = session?.IsExpired() ?? false;
        var hasCookie = !string.IsNullOrWhiteSpace(session?.Credentials.CookieHeader);
        var rejected = probe == SessionProbe.Rejected;
        var unreachable = probe == SessionProbe.Unreachable;
        return new
        {
            role = role.ToString(),
            profileId = profile?.Id,
            displayName = profile?.DisplayName,
            enabled = profile?.Enabled ?? false,
            hasCookie,
            expired,
            expiresAt = session?.Credentials.ExpiresAt,
            // true only when KoL itself answered with a rejection
            rejectedByServer = rejected,
            // true when the check could not reach KoL (timeout, 5xx); the session is then neither confirmed nor rejected
            serverUnreachable = unreachable,
            // null = unknown (KoL not reachable)
            usable = unreachable ? (bool?)null : hasCookie && !expired && !rejected,
            endpoint = session?.Profile.Endpoint?.ToString()
        };
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
            SessionProbe? probe = null;
            if (!string.IsNullOrWhiteSpace(session?.Credentials.CookieHeader) && !session!.IsExpired())
                probe = await ProbeSessionAsync(session.Credentials).ConfigureAwait(false);
            rows.Add(LoginStatusRow(role, profile, session, probe));
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
        var cleared = await LoginClear.ClearAsync(accounts, roles);
        foreach (var role in roles)
        {
            Console.Error.WriteLine(cleared.Contains(role)
                ? $"login clear: cleared credentials for {role} (profile retained)."
                : $"login clear: no profile for {role}.");
        }
        WriteJson(new { command = "login clear", roles = cleared.Select(r => r.ToString()).ToArray() });
        return 0;
    }

    private static async Task<int> LoginRefreshAsync(string[] args)
    {
        var role = StrictOptions.Role(args);
        var username = StrictOptions.Value(args, "--username");
        var password = StrictOptions.Value(args, "--password");
        var rememberFlag = HasFlag(args, "--no-remember-password") ? false : (bool?)null;
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync();
        var current = await accounts.GetSessionAsync(role, includeExpired: true);
        var storedPassword = !string.IsNullOrEmpty(current?.Credentials.Password);
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password) && storedPassword)
        {
            // No flags: log in again with the password saved for this role. It is never printed.
            var renewed = await accounts.ReconnectAsync(current!,
                (user, pass, token) => new KoLLoginClient().LoginAsync(user, pass, true, token));
            if (renewed is null)
                throw new ArgumentException($"Could not renew the session for {role}: the stored account changed meanwhile. Try again.");
            WriteJson(new { command = "login refresh", role = role.ToString(), ok = true, usedSavedPassword = true });
            return 0;
        }
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            // No saved password (or only one flag): ask on the terminal.
            if (Console.IsInputRedirected)
                throw new ArgumentException(
                    $"No password stored for {role} and input is not a terminal. Pass --username and --password, or run 'login refresh --role {role.ToString().ToLowerInvariant()}' in a terminal.");
            if (string.IsNullOrWhiteSpace(username))
            {
                var suggestion = current?.Credentials.Username ?? current?.Profile.DisplayName;
                Console.Error.Write($"login refresh {role} Username{(string.IsNullOrWhiteSpace(suggestion) ? "" : $" [{suggestion}]")}: ");
                username = Console.ReadLine()?.Trim();
                if (string.IsNullOrWhiteSpace(username)) username = suggestion;
            }
            if (string.IsNullOrEmpty(password))
                password = await ReadPasswordAsync();
        }
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("login refresh requires a username and a password.");

        // Keep the earlier choice: an account that had no password stored keeps having none, unless told otherwise.
        var rememberPassword = rememberFlag ?? (current is null || storedPassword);
        var login = await new KoLLoginClient().LoginAsync(username, password, rememberPassword);
        var verified = await ReadLoginIdentityAsync(login.Credentials)
            ?? throw new InvalidOperationException(
                "login refresh: KoL did not report which player this session belongs to; nothing was saved.");
        AccountIdentityChange change;
        try
        {
            change = await accounts.ApplyVerifiedLoginAsync(
                role, verified, login.Credentials, allowAccountChange: HasFlag(args, "--switch-account"));
        }
        catch (AccountIdentityMismatchException exception)
        {
            WriteJson(new { command = "login refresh", role = role.ToString(), ok = false, error = exception.Message });
            return 1;
        }
        WriteJson(new
        {
            command = "login refresh",
            role = role.ToString(),
            ok = true,
            rememberPassword,
            player = verified.Name,
            playerId = verified.PlayerId,
            accountChanged = change == AccountIdentityChange.Switched,
            loggedInAt = login.LoggedInAt
        });
        return 0;
    }

    private static async Task<KoLPlayerIdentity?> ReadLoginIdentityAsync(AccountCredentials credentials)
    {
        using var http = KoLLoginClient.CreateAuthenticatedClient(credentials, new Uri("https://www.kingdomofloathing.com/"));
        return await KoLPwdHashResolver.TryReadIdentityAsync(http);
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
                Console.Error.WriteLine("token: subcommand must be mint or show.");
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
