
namespace KoLibri.Desktop;

/// <summary>What the Browser tab does before it starts a relay: reuse the session, renew it, or ask for a sign-in.</summary>
public sealed record BrowserGateResult(bool CanStart, bool OfferSignIn, string Message);

/// <summary>
/// The Browser tab needs a usable KoL session. A stored one is used as is; a stale one is renewed from the stored password
/// when "Log in again automatically" is on (one login request through the global limiter, never retried here);
/// otherwise the tab shows why and offers Sign in instead of an unexplained dead end.
/// </summary>
public static class BrowserGate
{
    public static async Task<BrowserGateResult> CheckAsync(DesktopAccounts accounts, AccountRole role, bool autoLogin,
        CancellationToken cancellationToken = default)
    {
        var r = await StartupLogin.RunAsync(accounts, role, autoLogin, cancellationToken).ConfigureAwait(false);
        if (r.HasSession) return new(true, false, r.Message);
        var text = r.Outcome == StartupLoginOutcome.NothingStored && r.Message.Contains("not signed in", StringComparison.Ordinal)
            ? $"{role} is not signed in. Sign in to adventure in the browser."
            : r.Message;
        return new(false, true, text);
    }
}
