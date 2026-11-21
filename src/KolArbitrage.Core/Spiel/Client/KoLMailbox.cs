using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Basis;

namespace KolArbitrage.Core.Spiel;

public sealed record KoLMailEntry(string Id, string Sender, string Date, string Text)
{
    public string? Recipient { get; init; }
    public long AttachedMeat { get; init; }
    public bool HasAttachedMeat => AttachedMeat > 0;
    public string AttachedMeatLabel => $"{AttachedMeat:N0} Meat";
    public string Correspondent => Recipient is null ? Sender : $"To {Recipient}";
}

public sealed record KoLMailboxSnapshot(bool Recognized, IReadOnlyList<KoLMailEntry> Entries)
{
    public int Page { get; init; } = 1;
    public bool HasNextPage { get; init; }
}

public enum KoLMailFolder { Inbox, Saved, Outbox }

public sealed record KoLMailAttachment(int ItemId, int Quantity);

public static partial class KoLMailbox
{
    public static KoLMailboxSnapshot ParseInbox(string html) => ParseFolder(html, KoLMailFolder.Inbox);

    internal static KoLMailboxSnapshot ParseFolder(string html, KoLMailFolder folder)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Contains("There are no messages in this mailbox.", StringComparison.OrdinalIgnoreCase))
            return new(true, Array.Empty<KoLMailEntry>());

        var rows = new List<KoLMailEntry>();
        foreach (Match match in MessageCell().Matches(html))
        {
            var header = Checkbox().Match(match.Groups["body"].Value);
            if (!header.Success) continue;
            var mail = MailText().Match(match.Groups["body"].Value[(header.Index + header.Length)..]);
            if (!mail.Success) continue;
            var correspondent = Clean(mail.Groups["contact"].Value).Replace("[reply]", "", StringComparison.OrdinalIgnoreCase).Trim();
            var body = mail.Groups["text"].Value;
            var outgoing = folder == KoLMailFolder.Outbox;
            long attachedMeat = 0;
            if (!outgoing)
            {
                var attachment = MeatAttachment().Match(body);
                if (attachment.Success && long.TryParse(attachment.Groups["amount"].Value,
                    NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out attachedMeat) && attachedMeat > 0)
                    body = body.Remove(attachment.Index, attachment.Length);
            }
            rows.Add(new KoLMailEntry(header.Groups["id"].Value, outgoing ? "" : correspondent,
                Clean(mail.Groups["date"].Value), Clean(body))
            {
                Recipient = outgoing ? correspondent : null,
                AttachedMeat = attachedMeat
            });
        }
        var heading = FolderHeading().Matches(html).FirstOrDefault(match =>
            match.Groups["folder"].Value.Equals(folder.ToString(), StringComparison.OrdinalIgnoreCase));
        if (heading is null && folder != KoLMailFolder.Inbox)
            return new(false, Array.Empty<KoLMailEntry>());

        return new(rows.Count > 0, rows)
        {
            Page = int.TryParse(heading?.Groups["page"].Value, out var page) && page > 0 ? page : 1,
            HasNextPage = int.TryParse(heading?.Groups["last"].Value, out var last)
                && int.TryParse(heading?.Groups["total"].Value, out var total) && last < total
        };
    }

    public static Task<bool> SendTextAsync(
        HttpClient http, string sessionHash, string recipient, string message,
        CancellationToken cancellationToken = default)
        => SendAsync(http, sessionHash, recipient, message, [], 0, cancellationToken);

    public static async Task<bool> SendAsync(
        HttpClient http, string sessionHash, string recipient, string message,
        IReadOnlyList<KoLMailAttachment> attachments, long meat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentOutOfRangeException.ThrowIfNegative(meat);
        if (attachments.Count > 11 || attachments.Any(item => item.ItemId <= 0 || item.Quantity <= 0)
            || attachments.Select(item => item.ItemId).Distinct().Count() != attachments.Count)
            throw new ArgumentException("Use up to 11 distinct items with positive IDs and quantities.", nameof(attachments));
        if (attachments.Count == 0 && meat == 0)
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var endpoint = new Uri("https://www.kingdomofloathing.com/sendmessage.php");
        var security = new KoLSecurityPolicy();
        var fields = new Dictionary<string, string>
        {
            ["action"] = "send",
            ["towho"] = recipient.Trim(),
            ["message"] = message,
            ["savecopy"] = "on",
            ["pwd"] = sessionHash
        };
        for (var index = 0; index < attachments.Count; index++)
        {
            var suffix = (index + 1).ToString(CultureInfo.InvariantCulture);
            fields["whichitem" + suffix] = attachments[index].ItemId.ToString(CultureInfo.InvariantCulture);
            fields["howmany" + suffix] = attachments[index].Quantity.ToString(CultureInfo.InvariantCulture);
        }
        if (meat > 0)
            fields["sendmeat"] = meat.ToString(CultureInfo.InvariantCulture);

        using var form = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(endpoint, form, cancellationToken).ConfigureAwait(false);
        security.EnsureResponseUri(endpoint, response.RequestMessage?.RequestUri);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return SentConfirmation().IsMatch(html);
    }

    [GeneratedRegex("""<img\s+src=["'](?:https?://[^/"']+\.cloudfront\.net|https?://images\.kingdomofloathing\.com|/images)/itemimages/meat\.gif["']\s+height=30\s+width=30\s+alt="Meat"\s*>\s*You gain (?<amount>[0-9,]+) Meat\b""", RegexOptions.IgnoreCase)]
    private static partial Regex MeatAttachment();

    [GeneratedRegex(@"<center>\s*Message\s+(?:successfully\s+)?sent\b", RegexOptions.IgnoreCase)]
    private static partial Regex SentConfirmation();

    [GeneratedRegex(@"Messages:\s*(?<folder>Inbox|Saved|Outbox),\s*page\s+(?<page>\d+)\s*\(\s*(?:\d+\s*-\s*(?<last>\d+)\s+of\s+(?<total>\d+)|\d+\s+messages?)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex FolderHeading();

    private static string Clean(string html)
        => WebUtility.HtmlDecode(Tags().Replace(LineBreak().Replace(html, "\n"), "")).Trim();

    [GeneratedRegex("""<td\s+valign\s*=\s*["']?top["']?[^>]*>(?<body>.*?)(?=<td\s+valign\s*=\s*["']?top\b|<b>X</b>|$)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MessageCell();

    [GeneratedRegex("""<input\b[^>]*\bname\s*=\s*["'](?<id>\d+)["'][^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex Checkbox();

    [GeneratedRegex("""<b>\s*(?:From|To):?\s*</b>\s*(?<contact>.*?)<b>\s*Date:?\s*</b>\s*(?<date>.*?)(?:<br\s*/?>|</td>)(?<text>.*)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MailText();

    [GeneratedRegex("""<br\s*/?>|</p>""", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex Tags();
}
