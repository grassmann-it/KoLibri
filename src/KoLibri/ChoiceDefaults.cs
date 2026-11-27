using System.Globalization;
using System.Text.Json;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri;

/// <summary>Eine Option eines kuratierten Choice-Eintrags. Alle Angaben optional: nur Belegtes steht drin.</summary>
/// <remarks>
/// Die Flags (ab Version 2) kommen aus KoLmafias Spoilern und Kosten: <see cref="CostsMeat"/>/<see cref="CostsItem"/>,
/// <see cref="StartsFight"/>, <see cref="Hurts"/> (z.B. „get Beaten Up“), <see cref="SkipsTurn"/> („skip adventure“).
/// </remarks>
public sealed record ChoiceDefaultOption(string? Label, bool? CostsTurn, bool? Safe)
{
    /// <summary>Kostet Meat (KoLmafia-ChoiceCost oder Spoilertext).</summary>
    public bool CostsMeat { get; init; }

    /// <summary>Verbraucht Items (oder andere Ressourcen wie MP) laut KoLmafia.</summary>
    public bool CostsItem { get; init; }

    /// <summary>Startet einen Kampf.</summary>
    public bool StartsFight { get; init; }

    /// <summary>Schadet dem Charakter (Beaten Up, HP-Verlust).</summary>
    public bool Hurts { get; init; }

    /// <summary>Überspringt das Adventure (verlässt die Choice, laut KoLmafia ohne Gewinn).</summary>
    public bool SkipsTurn { get; init; }

    /// <summary>Items, die die Option laut KoLmafia bringt.</summary>
    public IReadOnlyList<string> Items { get; init; } = [];
}

/// <summary>Ein Eintrag aus choices.json: Choice-Id, belegte Standard-Option (oder null), Quelle.</summary>
public sealed record ChoiceDefault(
    int Id,
    string? Name,
    int? Snarfblat,
    int? Default,
    IReadOnlyDictionary<int, ChoiceDefaultOption> Options,
    string Source,
    string? Note,
    bool Turtle)
{
    /// <summary>Confidence-Wert für live verifizierte Einträge (schlagen KoLmafia immer).</summary>
    public const string LiveConfidence = "live";

    /// <summary>Confidence-Wert für aus KoLmafia importierte Einträge (Default nur, wenn die Option sicher ist).</summary>
    public const string KolmafiaConfidence = "kolmafia-default";

    /// <summary><see cref="LiveConfidence"/> (Standard, auch für Version 1) oder <see cref="KolmafiaConfidence"/>.</summary>
    public string Confidence { get; init; } = LiveConfidence;

    /// <summary>Ort laut KoLmafia (Location-Name), falls bekannt.</summary>
    public string? Location { get; init; }

    /// <summary>KoLmafia-Zone, falls bekannt.</summary>
    public string? Zone { get; init; }

    /// <summary>True für live verifizierte Einträge.</summary>
    public bool IsLive => Confidence == LiveConfidence;
}

/// <summary>Choice-Tabelle (KoLibri-eigenes Format): live verifizierte Einträge plus importierte KoLmafia-Spoiler.</summary>
public sealed class ChoiceDefaultTable
{
    private readonly Dictionary<int, ChoiceDefault> _byId;

    internal ChoiceDefaultTable(int version, IEnumerable<ChoiceDefault> entries)
    {
        Version = version;
        Entries = entries.ToArray();
        _byId = Entries.ToDictionary(e => e.Id);
    }

    public int Version { get; }
    public IReadOnlyList<ChoiceDefault> Entries { get; }

    public bool TryGet(int choiceId, out ChoiceDefault entry) => _byId.TryGetValue(choiceId, out entry!);

    /// <summary>
    /// Overrides vor Tabellen-Defaults vor Ask. Eine Default-Option, die nicht sichtbar ist, ergibt Ask.
    /// KoLmafia-Defaults (confidence kolmafia-default) gelten nur, wenn die Option als sicher markiert ist
    /// (kein Meat/Item, kein Kampf, kein Schaden); sonst Ask mit den Spoilern im Grund.
    /// Turtle-Choices ohne belegte Option werden standardmäßig durchgeklickt (erste sichtbare Option, UNBELEGT,
    /// live prüfen); mit <paramref name="stopOnTurtle"/> stoppen sie stattdessen mit Ask.
    /// </summary>
    public IChoiceResolver Resolver(IReadOnlyDictionary<int, int>? overrides = null, bool stopOnTurtle = false)
        => new TableResolver(this, overrides, stopOnTurtle);

    private sealed class TableResolver(ChoiceDefaultTable table, IReadOnlyDictionary<int, int>? overrides, bool stopOnTurtle) : IChoiceResolver
    {
        public ChoiceDecision Resolve(ChoiceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var id = context.ChoiceId;
            var visible = context.Options.Select(o => o.Option).ToHashSet();

            if (overrides is not null && overrides.TryGetValue(id, out var forced))
            {
                return visible.Contains(forced)
                    ? new ChoiceDecision(forced, ChoiceDecisionSource.Configured, $"Vorgabe für Choice {id}: Option {forced}.")
                    : Ask(context, $"Vorgegebene Option {forced} für Choice {id} ist auf der Seite nicht sichtbar.");
            }

            var known = table.TryGet(id, out var entry);
            string? held = null;
            if (known && entry.Default is { } option)
            {
                if (entry.IsLive)
                {
                    return visible.Contains(option)
                        ? new ChoiceDecision(option, ChoiceDecisionSource.BuiltIn,
                            $"Standard für Choice {id}: Option {option} ({entry.Source}).")
                        : Ask(context, $"Standard-Option {option} für Choice {id} ist auf der Seite nicht sichtbar.");
                }

                entry.Options.TryGetValue(option, out var opt);
                if (!visible.Contains(option))
                    held = $"KoLmafia-Standard Option {option} für Choice {id} ist auf der Seite nicht sichtbar.";
                else if (opt?.Safe != true)
                    held = $"KoLmafia-Standard Option {option} für Choice {id} wird nicht automatisch genommen ({Risk(opt)}).";
                else
                    return new ChoiceDecision(option, ChoiceDecisionSource.BuiltIn,
                        $"KoLmafia-Standard für Choice {id}{NamePart(entry)}: Option {option} „{opt.Label}“{(opt.SkipsTurn ? " (überspringt)" : "")} ({entry.Source}).");
            }

            // Turtle ohne nutzbare Option: durchklicken, außer der Aufrufer will stoppen.
            // UNBELEGT, live prüfen: Core und 0.4 kennen keine Turtle-Option, genommen wird die erste sichtbare.
            if (!stopOnTurtle && IsTurtle(context) && visible.Count > 0)
            {
                var first = visible.Min();
                return new ChoiceDecision(first, ChoiceDecisionSource.BuiltIn,
                    $"turtle: standardmäßig durchklicken (Choice {id}, Option {first}; unbelegt, live prüfen).");
            }

            if (!known) return Ask(context, $"Choice {id} ist nicht in der Tabelle.");
            var reason = held ?? $"Für Choice {id} ist keine Standard-Option hinterlegt.";
            var spoilers = Spoilers(entry);
            return Ask(context, spoilers.Length == 0
                ? reason
                : $"{reason} Optionen{NamePart(entry)} laut {entry.Source}: {spoilers}."
                    + (IsTurtle(context) ? "" : $" Festlegen mit --choice-action {id}:<N>."));
        }

        private static string NamePart(ChoiceDefault entry)
            => entry.Name is { Length: > 0 } n ? $" („{n}“)" : "";

        private static string Risk(ChoiceDefaultOption? o)
        {
            if (o is null) return "Option ohne Spoiler";
            var parts = new List<string>();
            if (o.CostsMeat) parts.Add("kostet Meat");
            if (o.CostsItem) parts.Add("kostet Items");
            if (o.StartsFight) parts.Add("startet Kampf");
            if (o.Hurts) parts.Add("schadet");
            if (parts.Count == 0) parts.Add(string.IsNullOrWhiteSpace(o.Label) ? "Option ohne Spoiler" : "nicht als sicher markiert");
            return string.Join(", ", parts);
        }

        private static string Spoilers(ChoiceDefault entry)
            => string.Join("; ", entry.Options
                .Where(p => !string.IsNullOrWhiteSpace(p.Value.Label))
                .OrderBy(p => p.Key)
                .Select(p => $"{p.Key} = {p.Value.Label}" + Flags(p.Value)));

        private static string Flags(ChoiceDefaultOption o)
        {
            var f = new List<string>();
            if (o.CostsMeat) f.Add("Meat");
            if (o.CostsItem) f.Add("Items");
            if (o.StartsFight) f.Add("Kampf");
            if (o.Hurts) f.Add("Schaden");
            if (o.SkipsTurn) f.Add("überspringt");
            return f.Count == 0 ? "" : $" [{string.Join(", ", f)}]";
        }

        private bool IsTurtle(ChoiceContext context)
            => Encounters.IsTurtleEncounterTitle(context.Title)
                || (table.TryGet(context.ChoiceId, out var e) && e.Turtle);

        private ChoiceDecision Ask(ChoiceContext context, string reason)
        {
            return ChoiceDecision.Ask(IsTurtle(context)
                ? CoreSpiel.TurtleTamerEncounters.DecisionReason(
                    context.ChoiceId.ToString(CultureInfo.InvariantCulture), context.Title, reason)
                    + $" Für `adventure run`: --choice-action {context.ChoiceId}:<N>."
                : reason);
        }
    }
}

/// <summary>Lädt die Choice-Defaults (eingebettete choices.json oder eine eigene Datei; Version 1 oder 2).</summary>
public static class ChoiceDefaults
{
    private const string ResourceName = "KoLibri.Data.choices.json";
    private static readonly Lazy<ChoiceDefaultTable> EmbeddedTable = new(LoadEmbedded);

    /// <summary>Die mit der Bibliothek ausgelieferte Tabelle (einmal geladen).</summary>
    public static ChoiceDefaultTable Embedded => EmbeddedTable.Value;

    /// <summary>
    /// Resolver über der eingebetteten Tabelle. Overrides schlagen Defaults, unbekannte Choices fragen.
    /// Turtle-Choices ohne Eintrag werden durchgeklickt (erste sichtbare Option, unbelegt); <paramref name="stopOnTurtle"/> stoppt sie.
    /// </summary>
    public static IChoiceResolver Resolver(IReadOnlyDictionary<int, int>? overrides = null, bool stopOnTurtle = false)
        => Embedded.Resolver(overrides, stopOnTurtle);

    /// <summary>Liest eine Tabelle. Kaputte oder unvollständige Dateien werfen <see cref="InvalidDataException"/> mit Grund.</summary>
    public static ChoiceDefaultTable Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("Wurzel ist kein Objekt.");
            var version = Int(root, "version") ?? throw Bad("'version' fehlt.");
            if (version is not (1 or 2)) throw Bad($"Unbekannte Version {version} (erwartet 1 oder 2).");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                throw Bad("'choices' fehlt oder ist kein Array.");

            var entries = new List<ChoiceDefault>();
            var seen = new HashSet<int>();
            var index = 0;
            foreach (var c in choices.EnumerateArray())
            {
                var where = $"choices[{index++}]";
                if (c.ValueKind != JsonValueKind.Object) throw Bad($"{where} ist kein Objekt.");
                var id = Int(c, "id", where) ?? throw Bad($"{where}: 'id' fehlt.");
                if (id <= 0) throw Bad($"{where}: 'id' muss positiv sein.");
                if (!seen.Add(id)) throw Bad($"{where}: Choice-Id {id} kommt doppelt vor.");
                var source = Str(c, "source", where);
                if (string.IsNullOrWhiteSpace(source)) throw Bad($"{where} (Choice {id}): 'source' fehlt.");

                var options = new Dictionary<int, ChoiceDefaultOption>();
                if (c.TryGetProperty("options", out var opts) && opts.ValueKind != JsonValueKind.Null)
                {
                    if (opts.ValueKind != JsonValueKind.Object) throw Bad($"{where} (Choice {id}): 'options' ist kein Objekt.");
                    foreach (var p in opts.EnumerateObject())
                    {
                        if (!int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
                            throw Bad($"{where} (Choice {id}): Optionsschlüssel '{p.Name}' ist keine positive Zahl.");
                        if (p.Value.ValueKind != JsonValueKind.Object)
                            throw Bad($"{where} (Choice {id}): Option {n} ist kein Objekt.");
                        var v = p.Value;
                        options[n] = new ChoiceDefaultOption(Str(v, "label", where), Bool(v, "costsTurn", where), Bool(v, "safe", where))
                        {
                            CostsMeat = Bool(v, "costsMeat", where) ?? false,
                            CostsItem = Bool(v, "costsItem", where) ?? false,
                            StartsFight = Bool(v, "startsFight", where) ?? false,
                            Hurts = Bool(v, "hurts", where) ?? false,
                            SkipsTurn = Bool(v, "skipsTurn", where) ?? false,
                            Items = Strings(v, "items", where),
                        };
                    }
                }

                var def = Int(c, "default", where);
                var turtle = Bool(c, "turtle", where) ?? false;
                if (def is { } d)
                {
                    if (d <= 0) throw Bad($"{where} (Choice {id}): 'default' muss positiv sein.");
                    if (turtle) throw Bad($"{where} (Choice {id}): Turtle-Choices dürfen kein 'default' haben.");
                    if (options.Count > 0 && !options.ContainsKey(d))
                        throw Bad($"{where} (Choice {id}): 'default' {d} steht nicht in 'options'.");
                }

                var confidence = Str(c, "confidence", where) ?? ChoiceDefault.LiveConfidence;
                if (confidence is not (ChoiceDefault.LiveConfidence or ChoiceDefault.KolmafiaConfidence))
                    throw Bad($"{where} (Choice {id}): unbekannte confidence '{confidence}' (erwartet live oder kolmafia-default).");

                entries.Add(new ChoiceDefault(id, Str(c, "name", where), Int(c, "snarfblat", where), def,
                    options, source!, Str(c, "note", where), turtle)
                {
                    Confidence = confidence,
                    Location = Str(c, "location", where),
                    Zone = Str(c, "zone", where),
                });
            }
            return new ChoiceDefaultTable(version, entries);
        }
        catch (JsonException e)
        {
            throw Bad("Kein gültiges JSON: " + e.Message, e);
        }
    }

    private static ChoiceDefaultTable LoadEmbedded()
    {
        using var stream = typeof(ChoiceDefaults).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Eingebettete Ressource {ResourceName} fehlt.");
        return Load(stream);
    }

    private static InvalidDataException Bad(string message, Exception? inner = null)
        => new("choices.json: " + message, inner);

    private static int? Int(JsonElement o, string name, string? where = null)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
            throw Bad($"{where ?? "Wurzel"}: '{name}' ist keine ganze Zahl.");
        return n;
    }

    private static string? Str(JsonElement o, string name, string where)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw Bad($"{where}: '{name}' ist kein Text.");
        return v.GetString();
    }

    private static string[] Strings(JsonElement o, string name, string where)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array) throw Bad($"{where}: '{name}' ist keine Liste.");
        return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
            ? e.GetString()!
            : throw Bad($"{where}: '{name}' enthält etwas anderes als Text.")).ToArray();
    }

    private static bool? Bool(JsonElement o, string name, string where)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad($"{where}: '{name}' ist kein Boolean.");
        return v.GetBoolean();
    }
}
