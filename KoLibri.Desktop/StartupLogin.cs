using KoLibri.Core.Basis;

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
/// never retried; the login client itself goes through the global rate limiter.
/// </summary>
public static class StartupLogin
{
    public static async Task<StartupLoginResult> RunAsync(
        AccountSessionManager accounts, AccountRole role, bool autoLogin,
        Func<string, string, CancellationToken, Task<KoLLoginResult>>? login = null,
        CancellationToken cancellationToken = default)
    {
        login ??= (user, password, token) => new KoLLoginClient().LoginAsync(user, password, true, token);
        try
        {
            await accounts.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var current = await accounts.GetSessionAsync(role, includeExpired: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
                return new(role, StartupLoginOutcome.NothingStored, $"{role}: not signed in.");
            var usable = !current.IsExpired() && !string.IsNullOrWhiteSpace(current.Credentials.CookieHeader);
            if (usable)
                return new(role, StartupLoginOutcome.AlreadySignedIn, $"{role}: signed in.");
            var hasPassword = !string.IsNullOrWhiteSpace(current.Credentials.Password)
                              && !string.IsNullOrWhiteSpace(current.Credentials.Username ?? current.Profile.DisplayName);
            if (!hasPassword)
                return new(role, StartupLoginOutcome.NothingStored,
                    $"{role}: the saved session has expired and no password is stored. Use Sign in.");
            if (!autoLogin)
                return new(role, StartupLoginOutcome.AutoLoginOff,
                    $"{role}: the saved session has expired. Use Sign in, or turn on automatic log-in in Settings.");

            var renewed = await accounts.ReconnectAsync(current, login, cancellationToken).ConfigureAwait(false);
            return renewed is not null && !string.IsNullOrWhiteSpace(renewed.Credentials.CookieHeader)
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
        AccountSessionManager accounts, AccountRole selected, bool autoLogin,
        Func<string, string, CancellationToken, Task<KoLLoginResult>>? login = null,
        Action<StartupLoginResult>? onEach = null, CancellationToken cancellationToken = default)
    {
        var results = new List<StartupLoginResult>();
        var other = selected == AccountRole.Main ? AccountRole.Mallbot : AccountRole.Main;
        foreach (var role in new[] { selected, other })
        {
            var r = await RunAsync(accounts, role, autoLogin, login, cancellationToken).ConfigureAwait(false);
            results.Add(r);
            onEach?.Invoke(r);
        }
        return results;
    }
}
