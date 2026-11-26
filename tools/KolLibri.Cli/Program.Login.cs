using System.Globalization;
using KoLibri;

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
        await using var client = CreateClient();
        // An explicit login with a name and password is the user's decision to (re)bind the role.
        await using var session = await client.LoginAsync(username, password, new LoginOptions
        {
            AccountId = AccountIdOf(role),
            RememberPassword = rememberPassword,
            AllowIdentityChange = true
        });
        if (session.Account.PlayerId is null)
            Console.Error.WriteLine("login: could not verify the player id; saving without it.");
        WriteJson(new { ok = true, role = role.ToString(), username, rememberPassword, loggedInAt = DateTimeOffset.UtcNow });
        return 0;
    }

    internal enum SessionProbe { Works, Rejected, Unreachable }

    /// <summary>
    /// Asks KoL whether the session still works. Only a login page, a 401/403 or a status answer without a player id
    /// counts as a rejection; timeouts, network errors and other HTTP errors (e.g. 503) leave the question open.
    /// The probe uses the action lane on purpose: there the library never logs in again by itself.
    /// </summary>
    internal static async Task<SessionProbe> ProbeSessionAsync(KolibriSession session)
    {
        try
        {
            var page = await session.GetPageAsync("api.php",
                new Dictionary<string, string> { ["what"] = "status", ["for"] = "KoLibri" }, RequestLane.Action).ConfigureAwait(false);
            return page.Html.Contains("playerid", StringComparison.OrdinalIgnoreCase) ? SessionProbe.Works : SessionProbe.Rejected;
        }
        catch (SessionExpiredException) { return SessionProbe.Rejected; }
        catch (UnexpectedPageException) { return SessionProbe.Unreachable; }
        catch (HttpRequestException) { return SessionProbe.Unreachable; }
        catch (TaskCanceledException) { return SessionProbe.Unreachable; }
    }

    internal static object LoginStatusRow(AccountRole role, Account? account, SessionProbe? probe, DateTimeOffset? now = null)
    {
        var expired = account?.SessionExpiresAt is { } at && at <= (now ?? DateTimeOffset.UtcNow);
        var hasCookie = account is not null && (account.HasSession || expired);
        var rejected = probe == SessionProbe.Rejected;
        var unreachable = probe == SessionProbe.Unreachable;
        return new
        {
            role = role.ToString(),
            profileId = account?.Id.Value,
            displayName = account?.PlayerName,
            enabled = account is not null,
            hasCookie,
            expired,
            expiresAt = account?.SessionExpiresAt,
            // true only when KoL itself answered with a rejection
            rejectedByServer = rejected,
            // true when the check could not reach KoL (timeout, 5xx); the session is then neither confirmed nor rejected
            serverUnreachable = unreachable,
            // null = unknown (KoL not reachable)
            usable = unreachable ? (bool?)null : hasCookie && !expired && !rejected,
            endpoint = (string?)null
        };
    }

    private static async Task<int> LoginStatusAsync(string[] args)
    {
        await using var client = CreateClient();
        var rows = new List<object>();
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            var account = (await client.Accounts.ListAsync()).FirstOrDefault(a => a.Id.Value == AccountIdOf(role));
            SessionProbe? probe = null;
            if (account is { HasSession: true })
            {
                await using var session = await client.OpenAsync(account.Id.Value);
                probe = await ProbeSessionAsync(session).ConfigureAwait(false);
            }
            rows.Add(LoginStatusRow(role, account, probe));
        }
        WriteJson(new
        {
            command = "login status",
            accounts = rows
        });
        return 0;
    }

    internal static async Task<int> LoginClearAsync(string[] args)
    {
        var roles = StrictOptions.LoginRoles(args);
        await using var client = CreateClient();
        var cleared = await LoginClear.ClearAsync(client.Accounts, roles);
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
        await using var client = CreateClient();
        var current = await client.Accounts.FindAsync(AccountIdOf(role));
        var storedPassword = current?.HasPassword == true;
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password) && storedPassword)
        {
            // No flags: log in again with the password saved for this role. It is never printed.
            await using var session = await client.OpenAsync(AccountIdOf(role));
            await session.ReconnectAsync();
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
                var suggestion = current?.PlayerName;
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
        KolibriSession renewed;
        try
        {
            renewed = await client.LoginAsync(username, password, new LoginOptions
            {
                AccountId = AccountIdOf(role),
                RememberPassword = rememberPassword,
                AllowIdentityChange = HasFlag(args, "--switch-account")
            });
        }
        catch (IdentityMismatchException exception)
        {
            WriteJson(new { command = "login refresh", role = role.ToString(), ok = false, error = exception.Message });
            return 1;
        }
        await using (renewed)
        {
            var account = renewed.Account;
            if (account.PlayerId is null)
                throw new InvalidOperationException(
                    "login refresh: KoL did not report which player this session belongs to; the session was stored without a verified player id.");
            WriteJson(new
            {
                command = "login refresh",
                role = role.ToString(),
                ok = true,
                rememberPassword,
                player = account.PlayerName,
                playerId = account.PlayerId?.ToString(CultureInfo.InvariantCulture),
                accountChanged = current?.PlayerId is { } before && before != account.PlayerId,
                loggedInAt = DateTimeOffset.UtcNow
            });
        }
        return 0;
    }

    private static async Task<int> TokenDispatchAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
        using var store = new ApprovalTokenStore(AgentGuard.TokenRootOverride ?? ApprovalTokenStore.DefaultRoot());
        switch (sub)
        {
            case "mint":
            case "new":
            {
                var label = GetOption(args[1..], "--label");
                var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var record = new ApprovalToken(Token: token, CreatedAt: DateTimeOffset.UtcNow, Label: label ?? "kolibri-cli");
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
}
