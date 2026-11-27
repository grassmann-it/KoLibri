using System.Text.Json;
using KoLibri;

namespace Cookbook.KmailBot;

public sealed class KmailBotOptions
{
    /// <summary>JSON-Datei mit bereits verarbeiteten Nachrichten-Ids; null = nur im Speicher.</summary>
    public string? StateFile { get; init; }
    /// <summary>Beantwortete Nachrichten zusätzlich im Saved-Ordner ablegen.</summary>
    public bool Save { get; init; }
    /// <summary>Ohne Yes wird nichts gesendet und nichts gemerkt.</summary>
    public bool Yes { get; init; }
}

public static class Recipe
{
    public const string Trigger = "!ping";

    public static async Task<int> RunAsync(KolibriSession session, KmailBotOptions options, TextWriter output, CancellationToken ct = default)
    {
        var seen = Load(options.StateFile);
        var page = await session.Mail.GetAsync(MailFolder.Inbox, 1, ct);
        output.WriteLine($"Inbox Seite {page.Page}/{page.PageCount}: {page.Entries.Count} Nachrichten, {seen.Count} schon verarbeitet.");

        var replied = 0;
        foreach (var mail in page.Entries)
        {
            if (seen.Contains(mail.Id)) continue;
            if (!mail.Text.Contains(Trigger, StringComparison.OrdinalIgnoreCase)) continue;

            if (!options.Yes)
            {
                output.WriteLine($"Würde {mail.From} (Nachricht {mail.Id}) mit \"pong\" antworten.");
                continue;
            }

            var sent = await session.Mail.SendAsync(new MailMessage(mail.From, "pong"), ct);
            output.WriteLine($"{mail.From}: {sent.Status}{(sent.Message is { Length: > 0 } m ? " - " + m : "")}");
            if (sent.Status != MailSendStatus.Sent) continue; // unklar/abgelehnt: nicht merken, nächster Lauf versucht es erneut
            replied++;
            seen.Add(mail.Id);
            if (options.Save) await session.Mail.SaveAsync(mail.Id, ct);
        }

        if (options.Yes) Store(options.StateFile, seen);
        output.WriteLine(options.Yes ? $"{replied} Antworten gesendet." : "Trockenlauf: nichts gesendet. Mit --yes antworten.");
        return 0;
    }

    private static HashSet<string> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new HashSet<string>();
        return new HashSet<string>(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>());
    }

    private static void Store(string? path, HashSet<string> seen)
    {
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(seen.Order().ToArray()));
    }
}
