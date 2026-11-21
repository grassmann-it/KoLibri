using System.Net;
using System.Text;
using LibKolibri.Access;

namespace KolLibri.Mcp;

/// <summary>
/// The consent page. All text comes from the structured request and the fixed catalog;
/// what the client says about itself is shown separately and escaped.
/// </summary>
public static class McpConsentPage
{
    public static readonly IReadOnlyDictionary<string, RightLimit> Defaults = new Dictionary<string, RightLimit>
    {
        [Rights.AccountRead] = new(),
        [Rights.MarketRead] = new(),
        [Rights.Adventure] = new() { DailyBudget = 20 },
        [Rights.MallBuy] = new() { DailyBudget = 100_000, MaxUnitPrice = 50_000, MeatReserve = 100_000 },
        [Rights.DailyRun] = new() { DailyBudget = 1 },
        [Rights.TransferMeat] = new() { DailyBudget = 0 },
        [Rights.TransferItems] = new() { DailyBudget = 10 },
        [Rights.MessageSend] = new() { DailyBudget = 5 },
        [Rights.ShopReprice] = new() { DailyBudget = 5, MaxChangePercent = 10 },
        [Rights.ItemUse] = new() { DailyBudget = 5 },
    };

    private static readonly Dictionary<string, string> Titles = new()
    {
        [Rights.AccountRead] = "Konto lesen (Status, Inventar, Pläne)",
        [Rights.MarketRead] = "Markt lesen (Mall-Preise)",
        [Rights.Adventure] = "Abenteuern",
        [Rights.MallBuy] = "In der Mall kaufen",
        [Rights.DailyRun] = "Tägliche Routine / Meme-Shop ausführen",
        [Rights.TransferMeat] = "Meat zwischen eigenen Konten senden",
        [Rights.TransferItems] = "Items zwischen eigenen Konten senden",
        [Rights.MessageSend] = "Nachrichten senden",
        [Rights.ShopReprice] = "Preise im eigenen Laden ändern",
        [Rights.ItemUse] = "Items verbrauchen",
    };

    private static readonly Dictionary<string, string> BudgetUnit = new()
    {
        [Rights.Adventure] = "Abenteuer pro Tag", [Rights.MallBuy] = "Meat pro Tag", [Rights.DailyRun] = "Läufe pro Tag",
        [Rights.TransferMeat] = "Meat pro Tag", [Rights.TransferItems] = "Items pro Tag",
        [Rights.MessageSend] = "Nachrichten pro Tag", [Rights.ShopReprice] = "Änderungen pro Tag", [Rights.ItemUse] = "Verbräuche pro Tag",
    };

    public static string Render(PendingConsent p, bool needsCode, string? error = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=de><meta charset=utf-8><title>KoLibri – Zugriff freigeben</title>")
          .Append("<style>body{font:15px system-ui;max-width:46rem;margin:2rem auto;padding:0 1rem;color:#222}")
          .Append("fieldset{border:1px solid #bbb;border-radius:6px;margin:.6rem 0;padding:.6rem .9rem}legend{font-weight:600}")
          .Append("input[type=number]{width:9rem}.warn{background:#fff3cd;padding:.6rem;border-radius:6px}")
          .Append(".err{background:#f8d7da;padding:.6rem;border-radius:6px}.said{color:#555;font-style:italic}")
          .Append("button{font:inherit;padding:.5rem 1.2rem;margin-right:.6rem}</style>");
        sb.Append("<h1>Zugriff für einen KI-Client freigeben</h1>");
        sb.Append("<p><b>").Append(H(p.Client.ClientName)).Append("</b> möchte auf deine KoL-Konten zugreifen.")
          .Append(" Rückkehr an: <code>").Append(H(Host(p.RedirectUri))).Append("</code></p>");
        if (p.Reason is not null) sb.Append("<p class=said>Der Client sagt: „").Append(H(p.Reason)).Append("“</p>");
        sb.Append("<p class=warn>Du bestimmst Rechte und Limits. Alles, was du hier nicht anhakst, bleibt gesperrt. ")
          .Append("Limits gelten pro KoL-Tag und zählen erst, wenn KoL die Aktion bestätigt hat.</p>");
        if (error is not null) sb.Append("<p class=err>").Append(H(error)).Append("</p>");

        sb.Append("<form method=post action=/authorize>");
        sb.Append("<input type=hidden name=session value=").Append(H(p.Id)).Append("><input type=hidden name=csrf value=").Append(H(p.Csrf)).Append('>');

        sb.Append("<fieldset><legend>Konten</legend>");
        foreach (var role in new[] { "Main", "Mallbot" })
            sb.Append("<label><input type=checkbox name=account value=").Append(role)
              .Append(p.RequestedAccounts.Count == 0 || p.RequestedAccounts.Contains(role, StringComparer.OrdinalIgnoreCase) ? " checked" : "")
              .Append("> ").Append(role).Append("</label> ");
        sb.Append("</fieldset>");

        foreach (var right in Rights.All)
        {
            var asked = p.RequestedRights.Contains(right);
            p.RequestedLimits.TryGetValue(right, out var wanted);
            var def = Defaults[right];
            sb.Append("<fieldset><legend><label><input type=checkbox name=right value=").Append(right).Append(asked ? " checked" : "")
              .Append("> ").Append(H(Titles[right])).Append("</label>").Append(asked ? "" : " <small>(nicht angefragt)</small>").Append("</legend>");
            if (BudgetUnit.TryGetValue(right, out var unit))
            {
                sb.Append("Limit: <input type=number min=0 name=budget_").Append(right).Append(" value=").Append(wanted?.DailyBudget ?? def.DailyBudget).Append("> ").Append(unit).Append(' ');
                if (right == Rights.MallBuy)
                    sb.Append("<br>Höchstpreis pro Stück: <input type=number min=0 name=maxprice_").Append(right).Append(" value=").Append(wanted?.MaxUnitPrice ?? def.MaxUnitPrice)
                      .Append("> Meat &nbsp; Meat-Reserve auf dem Konto: <input type=number min=0 name=reserve_").Append(right).Append(" value=").Append(wanted?.MeatReserve ?? def.MeatReserve).Append('>');
                if (right == Rights.ShopReprice)
                    sb.Append("<br>Größte Preisänderung: <input type=number min=0 max=100 name=pct_").Append(right).Append(" value=").Append(wanted?.MaxChangePercent ?? def.MaxChangePercent).Append("> %");
            }
            else sb.Append("<small>nur lesen, ohne Limit</small>");
            sb.Append("</fieldset>");
        }

        sb.Append("<fieldset><legend>Gültigkeit</legend><select name=hours>")
          .Append("<option value=1>1 Stunde</option><option value=24 selected>1 Tag</option><option value=168>7 Tage</option><option value=720>30 Tage</option></select></fieldset>");
        if (needsCode)
            sb.Append("<fieldset><legend>Bestätigungscode</legend>Der Code steht im Terminal, in dem <code>kolibri-mcp</code> läuft: ")
              .Append("<input name=code autocomplete=off inputmode=numeric size=8></fieldset>");
        sb.Append("<p><button name=decision value=approve>Freigeben</button><button name=decision value=deny>Ablehnen</button></p></form>");
        return sb.ToString();
    }

    private static string Host(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}") + (u.Scheme is "http" or "https" ? "" : " (" + u.Scheme + ")") : "?";
    private static string H(object? v) => WebUtility.HtmlEncode(v?.ToString() ?? "");
}
