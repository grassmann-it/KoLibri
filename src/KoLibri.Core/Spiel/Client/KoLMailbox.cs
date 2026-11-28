using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

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
    private static readonly KoLSecurityPolicy DefaultSecurityPolicy = new();

    /// <summary>Creates an allowlisted KMail client that owns the supplied handler and never follows redirects.</summary>
    public static HttpClient CreateTrustedClient(HttpMessageHandler inner, KoLSecurityPolicy? policy = null, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        policy ??= DefaultSecurityPolicy;
        policy.Validate();
        if (baseAddress is not null) policy.EnsureAllowed(baseAddress, "KMail send");
        KoLAllowlistHandler.EnsureNoAutoRedirect(inner);
        var http = new HttpClient(new KoLAllowlistHandler(inner, policy)) { BaseAddress = baseAddress };
        KoLAllowlistHandler.MarkTrusted(http);
        return http;
    }

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

    public static Task<bool> SendAsync(
        HttpClient http, string sessionHash, string recipient, string message,
        IReadOnlyList<KoLMailAttachment> attachments, long meat, CancellationToken cancellationToken = default)
        => SendAsync(http, sessionHash, recipient, message, attachments, meat, DefaultSecurityPolicy, cancellationToken);

    public static async Task<bool> SendAsync(
        HttpClient http, string sessionHash, string recipient, string message,
        IReadOnlyList<KoLMailAttachment> attachments, long meat, KoLSecurityPolicy securityPolicy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(securityPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentOutOfRangeException.ThrowIfNegative(meat);
        if (attachments.Count > 11 || attachments.Any(item => item.ItemId <= 0 || item.Quantity <= 0)
            || attachments.Select(item => item.ItemId).Distinct().Count() != attachments.Count)
            throw new ArgumentException("Use up to 11 distinct items with positive IDs and quantities.", nameof(attachments));
        if (attachments.Count == 0 && meat == 0)
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!KoLAllowlistHandler.IsTrusted(http))
            throw new KoLSecurityException("KMail requires an allowlisted client with automatic redirects disabled.");
        if (http.BaseAddress is { } baseAddress) securityPolicy.EnsureAllowed(baseAddress, "KMail send");
        var endpoint = new Uri(http.BaseAddress ?? new Uri("https://www.kingdomofloathing.com/"), "sendmessage.php");
        securityPolicy.EnsureAllowed(endpoint, "KMail send");
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

        var dispatched = new System.Runtime.CompilerServices.StrongBox<bool>();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) };
        request.Options.Set(KoLAllowlistHandler.DispatchedKey, dispatched);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseUri = response.RequestMessage?.RequestUri ?? endpoint;
            securityPolicy.EnsureResponseUri(endpoint, responseUri);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                responseUri = securityPolicy.ResolveRedirect(endpoint, location.OriginalString);
            if (responseUri.AbsolutePath.EndsWith("/login.php", StringComparison.OrdinalIgnoreCase))
                throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL redirected the KMail send to login.");
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
                throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page for the KMail send.");
            response.EnsureSuccessStatusCode();
            return SentConfirmation().IsMatch(html);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && dispatched.Value)
        {
            throw new KoLMailCanceledAfterSendException(exception, cancellationToken);
        }
    }

    /// <summary>
    /// Once the POST is on its way, a lost reply, a timeout, a 5xx or an
    /// unreadable answer is <see cref="KoLMailSendOutcome.Unconfirmed"/> (the mail may have arrived), never "not sent".
    /// Only a clear refusal (4xx, or a page naming a known refusal) is <see cref="KoLMailSendOutcome.NotSent"/>.
    /// Argument errors, caller cancellation, login pages and security violations propagate to the caller.
    /// </summary>
    public static Task<KoLMailSendOutcome> SendWithOutcomeAsync(
        HttpClient http, string sessionHash, string recipient, string message,
        IReadOnlyList<KoLMailAttachment> attachments, long meat, CancellationToken cancellationToken = default)
        => SendWithOutcomeAsync(http, sessionHash, recipient, message, attachments, meat, DefaultSecurityPolicy, cancellationToken);

    public static async Task<KoLMailSendOutcome> SendWithOutcomeAsync(
        HttpClient http, string sessionHash, string recipient, string message,
        IReadOnlyList<KoLMailAttachment> attachments, long meat, KoLSecurityPolicy securityPolicy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SendAsync(http, sessionHash, recipient, message, attachments, meat, securityPolicy, cancellationToken).ConfigureAwait(false)
                ? KoLMailSendOutcome.Sent : KoLMailSendOutcome.Unconfirmed;
        }
        catch (ArgumentException) { throw; }
        catch (KoLHtmlResponseException) { throw; }
        catch (KoLSecurityException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException e) when (e.StatusCode is { } code && (int)code is >= 400 and < 500 && (int)code != 408 && (int)code != 429)
        {
            return KoLMailSendOutcome.NotSent;                      // KoL answered with a client-error status: refused
        }
        catch
        {
            return KoLMailSendOutcome.Unconfirmed;                  // reply lost after the POST: unknown
        }
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

/// <summary>The caller cancelled after starting the KMail POST. Delivery may have occurred.</summary>
public sealed class KoLMailCanceledAfterSendException(OperationCanceledException inner, CancellationToken cancellationToken)
    : OperationCanceledException("KMail delivery is unknown because the send was cancelled.", inner, cancellationToken);

/// <summary>Result of a KMail send whose POST may have reached KoL.</summary>
public enum KoLMailSendOutcome
{
    /// <summary>KoL showed its "Message sent" confirmation.</summary>
    Sent,
    /// <summary>KoL certainly refused it.</summary>
    NotSent,
    /// <summary>The POST went out but the answer is missing or unreadable: the mail may have arrived.</summary>
    Unconfirmed
}
