
namespace KoLibri.Desktop;

public enum AdventureFailureKind
{
    Unknown,
    OpenChoice,
    OpenFight,
    Interstitial,
    NotLoggedIn,
    NoAdventures,
    LocationUnavailable,
    Redirect,
    Canceled
}

/// <summary>A failure in words the player can act on: what happened and the next step.</summary>
public sealed record AdventureFailureInfo(AdventureFailureKind Kind, string Message, string NextStep)
{
    /// <summary>One line for the status bar.</summary>
    public string Text => string.IsNullOrEmpty(NextStep) ? Message : $"{Message} {NextStep}";
}

/// <summary>
/// Turns KoLibri exceptions and run/stop messages into readable text. Never shows a bare
/// "302"; unknown failures keep their original message so nothing is hidden.
/// </summary>
public static class AdventureFailureMapper
{
    public static AdventureFailureInfo Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
            return new(AdventureFailureKind.Canceled, "The adventure request was canceled.", "Nothing more was sent.");
        if (exception is SessionExpiredException)
            return new(AdventureFailureKind.NotLoggedIn, "The session is not logged in any more.",
                "Log in again in Settings, then retry.");
        if (exception is ActionDeniedException denied)
            return new(AdventureFailureKind.Unknown, $"The safety guard refused the action: {denied.Reason}", "Nothing was sent.");
        if (exception is UnexpectedPageException { Page.Kind: PageKind.Maintenance })
            return new(AdventureFailureKind.Unknown, "KoL is in maintenance.", "Try again later.");
        if (exception is UnexpectedPageException { Page.Kind: PageKind.LoginRequired })
            return new(AdventureFailureKind.NotLoggedIn, "The session is not logged in any more.",
                "Log in again in Settings, then retry.");
        return Describe(exception.Message);
    }

    public static AdventureFailureInfo Describe(string? message)
    {
        var text = message?.Trim() ?? string.Empty;
        bool Has(string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

        if (Has("choice.php"))
            return new(AdventureFailureKind.OpenChoice, "A choice adventure is open on this account.",
                "Pick an option in the choice panel, or apply your choice policy.");
        if (Has("fight.php"))
            return new(AdventureFailureKind.OpenFight, "A fight is open on this account.",
                "Fight it (Fight encounter) or recheck, then start the adventure again.");
        if (Has("interstitial") || Has("main.php did not answer") || Has("to 'main.php"))
            return new(AdventureFailureKind.Interstitial,
                "KoL showed its one-time page after login (Daily Reminders / New Events) instead of the adventure.",
                "Open the character pane or press Refresh once, then try again.");
        if (Has("notloggedin") || Has("not logged in") || Has("login.php") || Has("session expired") || Has("sign in"))
            return new(AdventureFailureKind.NotLoggedIn, "The session is not logged in any more.",
                "Log in again in Settings, then retry.");
        if (Has("out of adventures") || Has("no adventures") || Has("NoAdventures") || Has("not enough adventures") || Has("too tired"))
            return new(AdventureFailureKind.NoAdventures, "No adventures left.",
                "Rest, eat, drink or wait for tomorrow's rollover.");
        if (Has("special route") || Has("friars") || Has("not available") || Has("unavailable")
            || Has("locked") || Has("can't get there") || Has("cannot be reached") || Has("requires "))
            return new(AdventureFailureKind.LocationUnavailable,
                text.Length > 0 && !Has("special route") ? text : "This location needs a special route and cannot be adventured in directly.",
                "Check the quest requirements or use the dedicated button (for example Friars).");
        if (Has("redirects are disabled") || Has("HTTP 30"))
            return new(AdventureFailureKind.Redirect, "KoL redirected the request to another page.",
                "Press Recheck encounter or Refresh, then try again.");
        return new(AdventureFailureKind.Unknown,
            text.Length == 0 ? "The adventure request failed for an unknown reason." : text, string.Empty);
    }
}
