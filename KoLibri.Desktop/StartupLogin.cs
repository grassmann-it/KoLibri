
namespace KoLibri.Desktop;

public enum StartupLoginOutcome
{
    /// <summary>A valid session already exists; nothing was sent.</summary>
    AlreadySignedIn,
    /// <summary>Nothing stored for this role (no profile, or neither session nor password).</summary>
    NothingStored,
    /// <summary>Stale or cookie-less session, but "Log in again automatically" is off. No request was made.</summary>
    AutoLoginOff,
    /// <summary>A fresh session was obtained and stored.</summary>
    LoggedIn,
    /// <summary>The attempt failed (network, wrong stored password, KoL refused). Not retried.</summary>
    Failed
}

public sealed record StartupLoginResult(AccountRole Role, StartupLoginOutcome Outcome, string Message)
{
    public bool HasSession => Outcome is StartupLoginOutcome.AlreadySignedIn or StartupLoginOutcome.LoggedIn;
}

/// <summary>
/// The decision made once at desktop start for each role: reuse the stored session, log in again from the stored
/// password (only when the setting is on), or leave it to the Sign in button. One KoL request at most per role,
/// never retried; the login goes through the library's limiter.
/// </summary>
public static class StartupLogin
{
    public static async Task<StartupLoginResult> RunAsync(
        DesktopAccounts accounts, AccountRole role, bool autoLogin,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await accounts.FindAsync(role, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return new(role, StartupLoginOutcome.NothingStored, $"{role}: not signed in.");
            if (current.HasSession && !await accounts.HasExpiredSessionAsync(role, cancellationToken).ConfigureAwait(false))
                return new(role, StartupLoginOutcome.AlreadySignedIn, $"{role}: signed in.");
            if (!current.HasPassword || string.IsNullOrWhiteSpace(current.PlayerName))
                return new(role, StartupLoginOutcome.NothingStored,
                    $"{role}: the saved session has expired and no password is stored. Use Sign in.");
            if (!autoLogin)
                return new(role, StartupLoginOutcome.AutoLoginOff,
                    $"{role}: the saved session has expired. Use Sign in, or turn on automatic log-in in Settings.");

            var renewed = await accounts.ReconnectAsync(role, cancellationToken).ConfigureAwait(false);
            return renewed is not null && !renewed.IsExpired
                ? new(role, StartupLoginOutcome.LoggedIn, $"{role}: signed in again.")
                : new(role, StartupLoginOutcome.Failed, $"{role}: automatic log-in did not work. Use Sign in.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Never show raw exception text: it can carry URLs or response fragments.
            return new(role, StartupLoginOutcome.Failed, $"{role}: automatic log-in failed (no connection or KoL refused). Use Sign in.");
        }
    }

    /// <summary>Selected role first, then the other one, strictly one after the other.</summary>
    public static async Task<IReadOnlyList<StartupLoginResult>> RunAllAsync(
        DesktopAccounts accounts, AccountRole selected, bool autoLogin,
        Action<StartupLoginResult>? onEach = null, CancellationToken cancellationToken = default)
    {
        var results = new List<StartupLoginResult>();
        var other = selected == AccountRole.Main ? AccountRole.Mallbot : AccountRole.Main;
        foreach (var role in new[] { selected, other })
        {
            var r = await RunAsync(accounts, role, autoLogin, cancellationToken).ConfigureAwait(false);
            results.Add(r);
            onEach?.Invoke(r);
        }
        return results;
    }
}
