using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Adventure;

/// <summary>Pure parser for read-only state on a KoL fight page.</summary>
public interface ICombatObservationParser
{
    CombatObservation Parse(string html, Uri? responseUri = null);
}

/// <summary>
/// Regex-based parser deliberately limited to observation. It accepts compact
/// fixture markup and the traditional KoL fight controls; it never submits a
/// form or follows a redirect.
/// </summary>
public partial class KoLCombatObservationParser : ICombatObservationParser
{
    private static readonly Regex TagPattern = new(
        "<(?<closing>/)?(?<name>[A-Za-z][A-Za-z0-9:_-]*)(?<attrs>[^>]*?)(?<self>/?)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FormPattern = new(
        "<form\\b(?<attrs>[^>]*)>(?<body>.*?)</form\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex OptionPattern = new(
        "<option\\b(?<attrs>[^>]*)>(?<text>.*?)</option\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ButtonPattern = new(
        "<(?<tag>button|input)\\b(?<attrs>[^>]*?)(?:>(?<text>.*?)</button\\s*>|/?>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptMonsterStatsPattern = new(
        "(?:var\\s+)?monsterstats\\s*=\\s*\\{(?<body>.*?)\\}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NumberPattern = new("[-+]?\\d[\\d,]*", RegexOptions.Compiled);
    private static readonly Regex PairPattern = new(
        "(?<current>\\d[\\d,]*)\\s*(?:/|of)\\s*(?<maximum>\\d[\\d,]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ScriptAssignmentPattern = new(
        "(?:var\\s+)?(?<name>[A-Za-z][A-Za-z0-9_-]*)\\s*[:=]\\s*['\"]?(?<value>\\d[\\d,]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QueryActionPattern = new(
        "(?:[?&])action=(?<action>attack|skill|useitem|item|runaway|run|flee|escape)(?:&[^\\s'\"]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public virtual CombatObservation Parse(string html, Uri? responseUri = null)
    {
        html ??= string.Empty;
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
        var advisories = new List<string>();
        var tags = ParseTags(html);

        var monsterName = ExtractMonsterName(html, tags);
        var monsterId = ExtractMonsterId(html, tags);
        var round = ExtractRound(html, tags);
        var monsterStats = ExtractMonsterStats(html);
        var playerHp = ExtractResource(html, tags, ResourceKind.PlayerHp);
        var playerMp = ExtractResource(html, tags, ResourceKind.PlayerMp);
        var enemyHp = monsterStats.Current is not null ? monsterStats : ExtractResource(html, tags, ResourceKind.EnemyHp);
        var enemyMp = ExtractResource(html, tags, ResourceKind.EnemyMp);
        var actions = ExtractActions(html, tags);
        var effects = ExtractEffects(html, tags);
        var familiar = ExtractFamiliar(html, tags);
        var outcome = ExtractOutcome(html, round, monsterName, actions);

        if (monsterName is null)
            advisories.Add("Combat response has no recognised monster name.");
        if (round is null)
            advisories.Add("Combat response has no recognised round marker.");
        if (actions.Count == 0 && outcome == CombatOutcome.Ongoing)
            advisories.Add("Combat response has no recognised legal actions.");
        if (outcome == CombatOutcome.Unknown)
            advisories.Add("Response did not match a known combat outcome.");

        return new CombatObservation
        {
            Monster = monsterName,
            MonsterName = monsterName,
            MonsterId = monsterId,
            MonsterDetails = new CombatMonster(
                monsterName, monsterId,
                enemyHp.Current, enemyHp.Maximum,
                enemyMp.Current, enemyMp.Maximum),
            Round = round ?? 0,
            PlayerHp = playerHp.Current,
            PlayerMaxHp = playerHp.Maximum,
            PlayerMp = playerMp.Current,
            PlayerMaxMp = playerMp.Maximum,
            EnemyHp = enemyHp.Current,
            EnemyMaxHp = enemyHp.Maximum,
            EnemyMp = enemyMp.Current,
            EnemyMaxMp = enemyMp.Maximum,
            AvailableActions = actions,
            Effects = effects,
            Familiar = familiar,
            Outcome = outcome,
            ResponseHash = hash,
            SourcePath = responseUri?.AbsolutePath,
            Advisories = advisories
        }.Normalize();
    }

    public CombatObservation ParseHtml(string html, Uri? responseUri = null)
        => Parse(html, responseUri);

    public CombatObservation ParsePage(string html, Uri? responseUri = null)
        => Parse(html, responseUri);

    private static List<HtmlTag> ParseTags(string html)
    {
        var result = new List<HtmlTag>();
        foreach (Match match in TagPattern.Matches(html))
        {
            if (match.Groups["closing"].Success)
                continue;
            var name = match.Groups["name"].Value;
            if (name.Equals("!doctype", StringComparison.OrdinalIgnoreCase)
                || name.Equals("html", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(new HtmlTag(name, match.Groups["attrs"].Value, match.Index,
                match.Index + match.Length, GetAttributes(match.Groups["attrs"].Value)));
        }
        return result;
    }

    private static string? ExtractMonsterName(string html, IReadOnlyList<HtmlTag> tags)
    {
        var preferredIds = new[] { "monname", "monstername", "monster-name", "enemyname", "enemy-name" };
        foreach (var id in preferredIds)
        {
            foreach (var tag in tags)
            {
                if (!HasIdOrClass(tag, id))
                    continue;
                var clean = TrimMonsterPrefix(CleanText(ElementText(html, tag)));
                if (!string.IsNullOrWhiteSpace(clean))
                    return clean;
            }
        }

        var fighting = Regex.Match(
            CleanText(html),
            @"(?:you(?:'re| are)\s+fighting|fighting)\s+(?<name>[A-Za-z0-9][^.!?\r\n<]{1,100})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (fighting.Success)
            return TrimMonsterPrefix(fighting.Groups["name"].Value);

        var scriptName = Regex.Match(
            html,
            "(?:monster(?:Name|name)|monname)\\s*[:=]\\s*['\\\"](?<name>[^'\\\"]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return scriptName.Success ? TrimMonsterPrefix(scriptName.Groups["name"].Value) : null;
    }

    private static string? ExtractMonsterId(string html, IReadOnlyList<HtmlTag> tags)
    {
        foreach (var tag in tags)
        {
            foreach (var name in new[] { "monsterid", "monster-id", "enemyid", "enemy-id" })
            {
                if (tag.Attributes.TryGetValue(name, out var value)
                    && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }

        var marker = Regex.Match(html,
            "(?:MONSTERID|monster_id|monsterId)\\s*[:=]\\s*['\\\"]?(?<id>\\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (marker.Success)
            return marker.Groups["id"].Value;

        var image = Regex.Match(html,
            "(?:src|data-src)\\s*=\\s*['\\\"][^'\\\"]*(?:/|\\b)mon(?<id>\\d+)\\.(?:gif|png|jpg)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return image.Success ? image.Groups["id"].Value : null;
    }

    private static int? ExtractRound(string html, IReadOnlyList<HtmlTag> tags)
    {
        var marker = Regex.Match(html,
            "(?:var\\s+)?(?:onturn|fightround|combatround|round)\\s*[:=]\\s*['\\\"]?(?<value>\\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (TryParseNumber(marker.Success ? marker.Groups["value"].Value : null, out var scriptRound))
            return Math.Max(0, scriptRound);

        foreach (var tag in tags)
        {
            if (!HasAnyName(tag, "onturn", "fightround", "combatround", "round", "turn"))
                continue;
            var candidate = tag.Attributes.TryGetValue("value", out var value)
                ? value
                : ElementText(html, tag);
            if (TryParseNumber(candidate, out var parsed))
                return Math.Max(0, parsed);
        }

        var textRound = Regex.Match(CleanText(html),
            @"(?:combat|fight)\s+round\s+(?<value>\d+)|\bround\s*[:#]?\s*(?<value2>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (textRound.Success)
        {
            var value = textRound.Groups["value"].Success
                ? textRound.Groups["value"].Value
                : textRound.Groups["value2"].Value;
            return TryParseNumber(value, out var parsed) ? Math.Max(0, parsed) : null;
        }
        return null;
    }

    private static ResourceValue ExtractMonsterStats(string html)
    {
        foreach (Match match in ScriptMonsterStatsPattern.Matches(html))
        {
            var body = match.Groups["body"].Value;
            var hp = ExtractNamedNumber(body, "hp", "hitpoints", "hit_points", "health");
            var maxHp = ExtractNamedNumber(body, "maxhp", "max_hp", "maxHealth", "maxhealth");
            var mp = ExtractNamedNumber(body, "mp", "mana", "mojo");
            var maxMp = ExtractNamedNumber(body, "maxmp", "max_mp", "maxMana", "maxmana");
            if (hp is not null || maxHp is not null || mp is not null || maxMp is not null)
                return new ResourceValue(hp, maxHp, mp, maxMp);
        }
        return ResourceValue.Empty;
    }

    private static ResourceValue ExtractResource(
        string html,
        IReadOnlyList<HtmlTag> tags,
        ResourceKind kind)
    {
        var identifiers = kind switch
        {
            ResourceKind.PlayerHp => new[] { "playerhp", "player-hp", "myhp", "currenthp", "current-hp", "hp", "health", "hitpoints", "hit-points" },
            ResourceKind.PlayerMp => new[] { "playermp", "player-mp", "mymp", "currentmp", "current-mp", "mp", "mana", "mojo", "mojopoints", "mojo-points" },
            ResourceKind.EnemyHp => new[] { "enemyhp", "enemy-hp", "monsterhp", "monster-hp", "monhp", "mon-hp", "enemyhealth", "enemy-health" },
            ResourceKind.EnemyMp => new[] { "enemymp", "enemy-mp", "monstermp", "monster-mp", "monmp", "mon-mp", "enemymana", "enemy-mana" },
            _ => Array.Empty<string>()
        };

        foreach (var tag in tags)
        {
            if (!HasAnyName(tag, identifiers))
                continue;
            if (kind is ResourceKind.PlayerHp or ResourceKind.PlayerMp
                && IsEnemyContext(tags, tag))
                continue;
            var value = ParseResource(ElementText(html, tag));
            if (value.Current is not null)
                return value;
            if (tag.Attributes.TryGetValue("value", out var attrValue))
            {
                value = ParseResource(attrValue);
                if (value.Current is not null)
                    return value;
            }
        }

        foreach (var tag in tags)
        {
            var attr = kind switch
            {
                ResourceKind.PlayerHp => new[] { "data-player-hp", "data-hp", "data-health" },
                ResourceKind.PlayerMp => new[] { "data-player-mp", "data-mp", "data-mana" },
                ResourceKind.EnemyHp => new[] { "data-enemy-hp", "data-monster-hp", "data-mon-hp" },
                ResourceKind.EnemyMp => new[] { "data-enemy-mp", "data-monster-mp", "data-mon-mp" },
                _ => Array.Empty<string>()
            };
            foreach (var name in attr)
            {
                if (tag.Attributes.TryGetValue(name, out var value))
                {
                    var parsed = ParseResource(value);
                    if (parsed.Current is not null)
                        return parsed;
                }
            }
        }

        var assignments = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ScriptAssignmentPattern.Matches(html))
        {
            if (TryParseNumber(match.Groups["value"].Value, out var value))
                assignments[match.Groups["name"].Value] = value;
        }
        var scriptNames = kind switch
        {
            ResourceKind.PlayerHp => new[] { "playerhp", "myhp", "currenthp", "hitpoints", "hp" },
            ResourceKind.PlayerMp => new[] { "playermp", "mymp", "currentmp", "manapoints", "mojopoints", "mp", "mana" },
            ResourceKind.EnemyHp => new[] { "enemyhp", "monsterhp", "monhp" },
            ResourceKind.EnemyMp => new[] { "enemymp", "monstermp", "monmp" },
            _ => Array.Empty<string>()
        };
        foreach (var name in scriptNames)
        {
            var current = assignments.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (current is not null)
            {
                var maximumName = "max" + name;
                var maximum = assignments.FirstOrDefault(x => x.Key.Equals(maximumName, StringComparison.OrdinalIgnoreCase)).Value;
                return new ResourceValue(current, maximum);
            }
        }

        var clean = CleanText(html);
        var contextual = kind switch
        {
            ResourceKind.PlayerHp => new[] { "player", "you", "your", "hp", "hit points", "health" },
            ResourceKind.PlayerMp => new[] { "player", "you", "your", "mp", "mojo", "mana" },
            ResourceKind.EnemyHp => new[] { "monster", "enemy", "opponent", "hp", "hit points", "health" },
            ResourceKind.EnemyMp => new[] { "monster", "enemy", "opponent", "mp", "mojo", "mana" },
            _ => Array.Empty<string>()
        };
        foreach (var label in contextual)
        {
            var match = Regex.Match(clean,
                $@"(?:{Regex.Escape(label)})[^0-9]{{0,48}}(?<value>\d[\d,]*)(?:\s*(?:/|of)\s*(?<maximum>\d[\d,]*))?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var parsed = ParseResource(match.Value);
                if (parsed.Current is not null)
                    return parsed;
            }
        }

        var generic = kind is ResourceKind.PlayerHp or ResourceKind.EnemyHp
            ? @"(?:hit\s*points?|health|hp)\s*[:=]\s*(?<value>\d[\d,]*)(?:\s*(?:/|of)\s*(?<maximum>\d[\d,]*))?"
            : @"(?:mojo\s*points?|mana|mp)\s*[:=]\s*(?<value>\d[\d,]*)(?:\s*(?:/|of)\s*(?<maximum>\d[\d,]*))?";
        var genericMatch = Regex.Match(clean, generic,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return genericMatch.Success ? ParseResource(genericMatch.Value) : ResourceValue.Empty;
    }

    private static bool IsEnemyContext(IReadOnlyList<HtmlTag> tags, HtmlTag candidate)
    {
        var prefix = tags.Where(x => x.Start <= candidate.Start).TakeLast(8);
        return prefix.Any(x => x.Attributes.TryGetValue("id", out var id)
                && (id.Contains("enemy", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("monster", StringComparison.OrdinalIgnoreCase))
            || x.Attributes.TryGetValue("class", out var cls)
                && (cls.Contains("enemy", StringComparison.OrdinalIgnoreCase)
                    || cls.Contains("monster", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<CombatAction> ExtractActions(string html, IReadOnlyList<HtmlTag> tags)
    {
        var actions = new List<CombatAction>();
        foreach (Match form in FormPattern.Matches(html))
        {
            var formAttrs = GetAttributes(form.Groups["attrs"].Value);
            var body = form.Groups["body"].Value;
            var bodyTags = ParseTags(body);
            var actionValue = FindValue(bodyTags, "action");
            if (actionValue is null && formAttrs.TryGetValue("name", out var formName))
                actionValue = formName;
            if (actionValue is null && formAttrs.TryGetValue("action", out var formAction))
            {
                var query = QueryActionPattern.Match(formAction);
                if (query.Success)
                    actionValue = query.Groups["action"].Value;
            }
            // KoL writes the form attributes without quotes, so the
            // attribute reader may have missed the name.
            if (actionValue is null)
            {
                actionValue = ReadUnquotedAttribute(form.Groups["attrs"].Value, "name");
                if (actionValue is not null
                    && !actionValue.Contains("action", StringComparison.OrdinalIgnoreCase)
                    && !actionValue.Contains("skill", StringComparison.OrdinalIgnoreCase)
                    && !actionValue.Contains("item", StringComparison.OrdinalIgnoreCase)
                    && !actionValue.Contains("run", StringComparison.OrdinalIgnoreCase))
                {
                    // The name is not an action kind — keep looking.
                    actionValue = null;
                }
            }

            var skillOptions = SelectOptions(body, bodyTags, "whichskill");
            var itemOptions = SelectOptions(body, bodyTags, "whichitem");
            var hiddenSkill = FindValue(bodyTags, "whichskill");
            var hiddenItem = FindValue(bodyTags, "whichitem");
            var controls = ButtonPattern.Matches(body).Cast<Match>()
                .Where(x => !HasAttribute(x.Groups["attrs"].Value, "disabled"))
                .ToArray();

            var kind = ActionKindFrom(actionValue);
            if (kind == CombatActionKind.Unknown)
            {
                foreach (var control in controls)
                {
                    var value = GetAttribute(control.Groups["attrs"].Value, "value")
                        ?? CleanText(control.Groups["text"].Value);
                    kind = ActionKindFrom(value);
                    if (kind != CombatActionKind.Unknown)
                        break;
                }
            }

            if (kind == CombatActionKind.Attack)
            {
                var label = controls.Select(x => GetAttribute(x.Groups["attrs"].Value, "value")
                        ?? CleanText(x.Groups["text"].Value))
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                AddAction(actions, new CombatAction(kind, null, label) { Value = actionValue ?? "attack" });
            }
            else if (kind == CombatActionKind.Skill)
            {
                if (skillOptions.Count > 0)
                    foreach (var option in skillOptions)
                        AddAction(actions, NewAction(kind, option.Id, option.Name, option.Name));
                else
                    AddAction(actions, NewAction(kind, ValidActionId(hiddenSkill), null, actionValue));
            }
            else if (kind == CombatActionKind.Item)
            {
                if (itemOptions.Count > 0)
                    foreach (var option in itemOptions)
                        AddAction(actions, NewAction(kind, option.Id, option.Name, option.Name));
                else
                    AddAction(actions, NewAction(kind, ValidActionId(hiddenItem), null, actionValue));
            }
            else if (kind == CombatActionKind.Run)
            {
                var label = controls.Select(x => GetAttribute(x.Groups["attrs"].Value, "value")
                        ?? CleanText(x.Groups["text"].Value))
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                AddAction(actions, new CombatAction(kind, null, label) { Value = actionValue ?? "runaway" });
            }
            else
            {
                if (skillOptions.Count > 0)
                    foreach (var option in skillOptions)
                        AddAction(actions, NewAction(CombatActionKind.Skill, option.Id, option.Name, "skill"));
                else if (ValidActionId(hiddenSkill) is { } skillId)
                    AddAction(actions, NewAction(CombatActionKind.Skill, skillId, null, "skill"));
                if (itemOptions.Count > 0)
                    foreach (var option in itemOptions)
                        AddAction(actions, NewAction(CombatActionKind.Item, option.Id, option.Name, "useitem"));
                else if (ValidActionId(hiddenItem) is { } itemId)
                    AddAction(actions, NewAction(CombatActionKind.Item, itemId, null, "useitem"));
            }
        }

        // Compact fixtures often omit a form body and place controls directly
        // in the page. Parse those controls as a fallback.
        foreach (var tag in tags)
        {
            if (!tag.Name.Equals("input", StringComparison.OrdinalIgnoreCase)
                && !tag.Name.Equals("button", StringComparison.OrdinalIgnoreCase))
                continue;
            if (HasAttribute(tag.RawAttributes, "disabled"))
                continue;
            var name = GetAttribute(tag.RawAttributes, "name");
            var value = GetAttribute(tag.RawAttributes, "value");
            var label = value ?? CleanText(ElementText(html, tag));
            var kind = name?.Equals("whichskill", StringComparison.OrdinalIgnoreCase) == true
                ? CombatActionKind.Skill
                : name?.Equals("whichitem", StringComparison.OrdinalIgnoreCase) == true
                    ? CombatActionKind.Item
                    : ActionKindFrom(value ?? label);
            if (name?.Equals("action", StringComparison.OrdinalIgnoreCase) == true
                || kind != CombatActionKind.Unknown)
            {
                if (kind is CombatActionKind.Skill or CombatActionKind.Item)
                    AddAction(actions, NewAction(kind, ValidActionId(value), label, value));
                else if (kind is CombatActionKind.Attack or CombatActionKind.Run)
                    AddAction(actions, new CombatAction(kind, null, label) { Value = value });
            }
        }

        foreach (Match query in QueryActionPattern.Matches(html))
        {
            var kind = ActionKindFrom(query.Groups["action"].Value);
            var suffixStart = query.Index + query.Length;
            var suffix = html[suffixStart..Math.Min(html.Length, suffixStart + 160)];
            var id = Regex.Match(suffix, "(?:whichskill|whichitem)=(?<id>[^&#'\\\"\\s>]+)",
                RegexOptions.IgnoreCase).Groups["id"].Value;
            AddAction(actions, NewAction(kind, ValidActionId(id), null, query.Groups["action"].Value));
        }
        return actions;
    }

    private static List<OptionValue> SelectOptions(string body, IReadOnlyList<HtmlTag> tags, string expected)
    {
        var options = new List<OptionValue>();
        foreach (var select in tags.Where(x => x.Name.Equals("select", StringComparison.OrdinalIgnoreCase)
                     && HasAnyName(x, expected)))
        {
            var element = ElementText(body, select);
            foreach (Match option in OptionPattern.Matches(element))
            {
                var attrs = GetAttributes(option.Groups["attrs"].Value);
                if (HasAttribute(option.Groups["attrs"].Value, "disabled"))
                    continue;
                var id = attrs.TryGetValue("value", out var value) ? ValidActionId(value) : null;
                if (id is null)
                    continue;
                var text = CleanText(option.Groups["text"].Value);
                if (string.IsNullOrWhiteSpace(text))
                    text = id;
                if (id.Equals("0", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("none", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("select", StringComparison.OrdinalIgnoreCase))
                    continue;
                options.Add(new OptionValue(id, text));
            }
        }
        return options;
    }

    private static CombatAction NewAction(CombatActionKind kind, string? id,
        string? name, string? value)
        => new(kind, id, name) { Value = value };

    private static void AddAction(List<CombatAction> actions, CombatAction action)
    {
        if (action.Kind == CombatActionKind.Unknown)
            return;
        var duplicate = actions.Any(x => x.Kind == action.Kind
            && string.Equals(x.Id, action.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, action.Name, StringComparison.OrdinalIgnoreCase));
        if (!duplicate)
            actions.Add(action);
    }

    private static List<CombatEffect> ExtractEffects(string html, IReadOnlyList<HtmlTag> tags)
    {
        var effects = new List<CombatEffect>();
        foreach (var tag in tags)
        {
            var id = tag.Attributes.TryGetValue("id", out var idValue) ? idValue : null;
            var cls = tag.Attributes.TryGetValue("class", out var classValue) ? classValue : null;
            var isEffect = (id?.Contains("effect", StringComparison.OrdinalIgnoreCase) ?? false)
                || (id?.Contains("buff", StringComparison.OrdinalIgnoreCase) ?? false)
                || (cls?.Contains("effect", StringComparison.OrdinalIgnoreCase) ?? false)
                || (cls?.Contains("buff", StringComparison.OrdinalIgnoreCase) ?? false)
                || (cls?.Contains("debuff", StringComparison.OrdinalIgnoreCase) ?? false);
            var title = GetAttribute(tag.RawAttributes, "data-effect-name")
                ?? GetAttribute(tag.RawAttributes, "effect")
                ?? GetAttribute(tag.RawAttributes, "title")
                ?? GetAttribute(tag.RawAttributes, "alt");
            if (!isEffect && string.IsNullOrWhiteSpace(title))
                continue;
            var text = CleanText(ElementText(html, tag));
            var name = CleanEffectName(title ?? text);
            if (string.IsNullOrWhiteSpace(name)
                || name.Equals("effect", StringComparison.OrdinalIgnoreCase)
                || name.Equals("effects", StringComparison.OrdinalIgnoreCase)
                || name.Equals("buff", StringComparison.OrdinalIgnoreCase)
                || name.Equals("debuff", StringComparison.OrdinalIgnoreCase))
                continue;
            var source = title is null ? text : title;
            AddEffect(effects, new CombatEffect(name, ExtractDuration(source),
                GetAttribute(tag.RawAttributes, "data-effect-id"),
                string.IsNullOrWhiteSpace(text) || text.Equals(name, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : text));
        }

        foreach (var tag in tags.Where(x => HasAnyName(x, "effects", "effect-list", "status-effects")))
        {
            var text = CleanText(ElementText(html, tag));
            foreach (var part in Regex.Split(text, @"[,;|]")
                         .Select(CleanEffectName)
                         .Where(x => !string.IsNullOrWhiteSpace(x)))
                AddEffect(effects, new CombatEffect(part, ExtractDuration(part)));
        }

        foreach (Match match in Regex.Matches(CleanText(html),
            @"(?:effect|buff|debuff)\s*[:\-]\s*(?<name>[A-Za-z0-9][^,;.!?\r\n]{1,80})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var name = CleanEffectName(match.Groups["name"].Value);
            if (!string.IsNullOrWhiteSpace(name))
                AddEffect(effects, new CombatEffect(name, ExtractDuration(name)));
        }
        return effects;
    }

    private static void AddEffect(List<CombatEffect> effects, CombatEffect effect)
    {
        if (string.IsNullOrWhiteSpace(effect.Name))
            return;
        if (effects.Any(x => x.Name.Equals(effect.Name, StringComparison.OrdinalIgnoreCase)))
            return;
        effects.Add(effect);
    }

    private static string? ExtractFamiliar(string html, IReadOnlyList<HtmlTag> tags)
    {
        foreach (var tag in tags)
        {
            if (!HasAnyName(tag, "familiar", "fam", "familiarname", "familiar-name"))
                continue;
            var value = GetAttribute(tag.RawAttributes, "data-name")
                ?? GetAttribute(tag.RawAttributes, "title")
                ?? GetAttribute(tag.RawAttributes, "alt")
                ?? CleanText(ElementText(html, tag));
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static CombatOutcome ExtractOutcome(string html, int? round,
        string? monster, IReadOnlyCollection<CombatAction> actions)
    {
        var text = CleanText(html);
        if (Regex.IsMatch(text,
            @"(?:you\s+(?:have\s+been\s+)?defeated|you\s+lose(?:\s+the\s+fight)?|you\s+died|you\s+are\s+dead|fight\s+lost|defeat)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return CombatOutcome.Defeat;
        if (Regex.IsMatch(text,
            @"(?:you\s+(?:ran\s+)?away|you\s+flee|you\s+get\s+away|you\s+escape|runaway\s+successful|escaped\s+from)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return CombatOutcome.Fled;
        if (Regex.IsMatch(text,
            @"(?:you\s+(?:have\s+)?won|you\s+win(?:\s+the\s+fight)?|victory|fight\s+(?:is\s+)?over|you\s+defeated|combat\s+complete)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return CombatOutcome.Victory;

        if (actions.Count > 0 || round is not null || !string.IsNullOrWhiteSpace(monster)
            || Regex.IsMatch(html, @"(?:fight\.php|combatround|onturn)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return CombatOutcome.Ongoing;
        return CombatOutcome.Unknown;
    }

}
