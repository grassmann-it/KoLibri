using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Markt;

public enum MallReceiptKind { Confirmed, Refused, Unknown }

public sealed record MallReceiptReading(MallReceiptKind Kind, string Message);

/// <summary>
/// Reads the page KoL answers a <c>mallstore.php</c> purchase POST with, following KoLmafia's
/// <c>MallPurchaseRequest</c>: a purchase shows an item table (<c>&lt;table class="item"</c>) and "You spent N Meat";
/// refusals are "You can't afford", "That player will not sell to you", "Its inventory is frozen",
/// "This store doesn't ..." / "failed to yield" (gone or repriced) and the per-day limit text. A page with both or
/// neither kind of marker is Unknown: the inventory then decides.
/// </summary>
public static class MallPurchaseReceiptReader
{
    private static readonly Regex Spent = new(@"You spent\s+([\d,]+)\s+Meat", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItemTable = new("<table[^>]*class=[\"']item[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Marker, string Message)[] Refusals =
    [
        ("You can't afford", "KoL says you cannot afford it (not enough Meat)."),
        ("That player will not sell to you", "The shop owner does not sell to this account."),
        ("Its inventory is frozen", "The shop's inventory is frozen."),
        ("This store doesn't", "The shop no longer has the item at that price (sold or repriced)."),
        ("failed to yield", "The shop failed to yield the item (sold or repriced)."),
        ("You may only buy", "The shop's per-day limit for this item is reached."),
        ("not enough meat", "Not enough Meat."),
        ("sold out", "The item is sold out."),
        ("no longer available", "The item is no longer available.")
    ];

    public static MallReceiptReading Read(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new(MallReceiptKind.Unknown, "KoL answered with an empty page.");
        var confirmed = Spent.IsMatch(body) || ItemTable.IsMatch(body)
            || body.Contains("You acquire", StringComparison.OrdinalIgnoreCase)
            || body.Contains("you bought", StringComparison.OrdinalIgnoreCase)
            || body.Contains("successfully purchased", StringComparison.OrdinalIgnoreCase)
            || body.Contains("purchase successful", StringComparison.OrdinalIgnoreCase)
            || body.Contains("you purchase", StringComparison.OrdinalIgnoreCase)
            || body.Contains("item added to your inventory", StringComparison.OrdinalIgnoreCase)
            || body.Contains("you got", StringComparison.OrdinalIgnoreCase);
        var refusal = Refusals.FirstOrDefault(r => body.Contains(r.Marker, StringComparison.OrdinalIgnoreCase));
        if (confirmed && refusal.Marker is null) return new(MallReceiptKind.Confirmed, "KoL acknowledged the Mall purchase.");
        if (!confirmed && refusal.Marker is not null) return new(MallReceiptKind.Refused, refusal.Message);
        return new(MallReceiptKind.Unknown, refusal.Marker is not null
            ? "KoL's answer contains both purchase and refusal text."
            : "KoL returned no unambiguous Mall purchase acknowledgement.");
    }
}

/// <summary>A short, redacted text view of a response for messages and logs: tags stripped, no secrets.</summary>
public static class MallPurchaseDiagnostics
{
    private static readonly Regex Scripts = new("<(script|style)\\b.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex Secrets = new(
        "(pwd|password|passwd|hash|cookie|token|sessionid|PHPSESSID)\\s*[=:]\\s*\\S+|\\b[0-9a-f]{24,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Snippet(string? body, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty)";
        var text = Scripts.Replace(body, " ");
        text = WebUtility.HtmlDecode(Tags.Replace(text, " "));
        text = Secrets.Replace(text, "[redacted]");
        text = Spaces.Replace(text, " ").Trim();
        if (text.Length == 0) return "(no text)";
        return text.Length <= max ? text : text[..max] + "…";
    }
}
