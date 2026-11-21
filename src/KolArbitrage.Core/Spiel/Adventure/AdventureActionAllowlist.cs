namespace KolArbitrage.Core.Spiel;

/// <summary>A single combat action that may be emitted for an encounter.</summary>
public sealed record AdventureFightActionRule(
    string ActionId,
    string? EncounterId = null,
    int? Round = null,
    string? MonsterId = null);

/// <summary>A single choice option that may be emitted for a choice page.</summary>
public sealed record AdventureChoiceActionRule(
    string ChoiceId,
    string OptionId,
    string? EncounterId = null);

/// <summary>
/// An immutable description of the actions an adapter is willing to send for
/// one adventure. Empty encounter ids mean that all recognised encounters in
/// that adventure are allowed; unknown parser output is still rejected by the
/// kernel before this policy is consulted.
/// </summary>
public sealed record AdventureAllowlistEntry
{
    public AdventureAllowlistEntry()
        : this(string.Empty)
    {
    }

    public AdventureAllowlistEntry(
        string adventureId,
        IEnumerable<string>? encounterIds = null,
        IEnumerable<AdventureFightActionRule>? fightActions = null,
        IEnumerable<AdventureChoiceActionRule>? choiceActions = null)
    {
        AdventureId = adventureId?.Trim() ?? string.Empty;
        EncounterIds = NormalizeSet(encounterIds);
        FightActions = (fightActions ?? Array.Empty<AdventureFightActionRule>())
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.ActionId))
            .Select(x => x with
            {
                ActionId = x.ActionId.Trim(),
                EncounterId = Normalize(x.EncounterId),
                MonsterId = Normalize(x.MonsterId),
                Round = x.Round is < 0 ? 0 : x.Round
            })
            .ToArray();
        ChoiceActions = (choiceActions ?? Array.Empty<AdventureChoiceActionRule>())
            .Where(x => x is not null
                && !string.IsNullOrWhiteSpace(x.ChoiceId)
                && !string.IsNullOrWhiteSpace(x.OptionId))
            .Select(x => x with
            {
                ChoiceId = x.ChoiceId.Trim(),
                OptionId = x.OptionId.Trim(),
                EncounterId = Normalize(x.EncounterId)
            })
            .ToArray();
    }

    public string AdventureId { get; init; }
    public IReadOnlySet<string> EncounterIds { get; init; }
    public IReadOnlyList<AdventureFightActionRule> FightActions { get; init; }
    public IReadOnlyList<AdventureChoiceActionRule> ChoiceActions { get; init; }

    public bool AllowsEncounter(string? encounterId)
    {
        if (EncounterIds.Count == 0) return true;
        return !string.IsNullOrWhiteSpace(encounterId)
            && EncounterIds.Contains(encounterId.Trim());
    }

    private static HashSet<string> NormalizeSet(IEnumerable<string>? values)
        => new((values ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()), StringComparer.Ordinal);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Pure allowlist policy for the adventure kernel. It has no URL or HTTP
/// knowledge; it only maps observed ids to concrete, approved actions.
/// </summary>
public sealed class AdventureActionAllowlist
{
    private readonly Dictionary<string, AdventureAllowlistEntry> _entries;

    public AdventureActionAllowlist(IEnumerable<AdventureAllowlistEntry>? entries = null)
    {
        _entries = new Dictionary<string, AdventureAllowlistEntry>(StringComparer.Ordinal);
        foreach (var entry in entries ?? Array.Empty<AdventureAllowlistEntry>())
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (string.IsNullOrWhiteSpace(entry.AdventureId)) continue;
            _entries[entry.AdventureId.Trim()] = new AdventureAllowlistEntry(
                entry.AdventureId,
                entry.EncounterIds is null ? new HashSet<string>(StringComparer.Ordinal) : entry.EncounterIds,
                entry.FightActions is null ? Array.Empty<AdventureFightActionRule>() : entry.FightActions,
                entry.ChoiceActions is null ? Array.Empty<AdventureChoiceActionRule>() : entry.ChoiceActions);
        }
    }

    public bool WaitForManualChoice { get; init; }

    public static AdventureActionAllowlist Empty => new();

    public IReadOnlyCollection<AdventureAllowlistEntry> Entries
        => _entries.Values.ToArray();

    /// <summary>Returns a new policy allowing the adventure with no id restrictions.</summary>
    public AdventureActionAllowlist WithAdventure(string adventureId)
    {
        var copy = Clone();
        copy.AddAdventure(adventureId);
        return copy;
    }

    /// <summary>Mutating builder convenience; the kernel itself never mutates a policy.</summary>
    public AdventureActionAllowlist AddAdventure(string adventureId)
    {
        var id = Require(adventureId, nameof(adventureId));
        if (!_entries.ContainsKey(id)) _entries[id] = new AdventureAllowlistEntry(id);
        return this;
    }

    public AdventureActionAllowlist AllowAdventure(string adventureId)
        => AddAdventure(adventureId);

    public AdventureActionAllowlist WithEncounter(string adventureId, string encounterId)
    {
        var copy = Clone();
        copy.AddEncounter(adventureId, encounterId);
        return copy;
    }

    public AdventureActionAllowlist AddEncounter(string adventureId, string encounterId)
    {
        var adventure = GetOrCreate(adventureId);
        var encounter = Require(encounterId, nameof(encounterId));
        _entries[adventure.AdventureId] = Rebuild(
            adventure,
            encounterIds: adventure.EncounterIds.Append(encounter));
        return this;
    }

    public AdventureActionAllowlist AllowEncounter(string adventureId, string encounterId)
        => AddEncounter(adventureId, encounterId);

    public AdventureActionAllowlist WithFightAction(
        string adventureId,
        string actionId,
        string? encounterId = null,
        int? round = null,
        string? monsterId = null)
    {
        var copy = Clone();
        copy.AddFightAction(adventureId, actionId, encounterId, round, monsterId);
        return copy;
    }

    public AdventureActionAllowlist AddFightAction(
        string adventureId,
        string actionId,
        string? encounterId = null,
        int? round = null,
        string? monsterId = null)
    {
        var adventure = GetOrCreate(adventureId);
        var rule = new AdventureFightActionRule(actionId, encounterId, round, monsterId);
        _entries[adventure.AdventureId] = Rebuild(
            adventure,
            fightActions: adventure.FightActions.Append(rule));
        return this;
    }

    public AdventureActionAllowlist AllowFightAction(
        string adventureId,
        string actionId,
        string? encounterId = null,
        int? round = null,
        string? monsterId = null)
        => AddFightAction(adventureId, actionId, encounterId, round, monsterId);

    public AdventureActionAllowlist WithChoiceOption(
        string adventureId,
        string choiceId,
        string optionId,
        string? encounterId = null)
    {
        var copy = Clone();
        copy.AddChoiceOption(adventureId, choiceId, optionId, encounterId);
        return copy;
    }

    public AdventureActionAllowlist AddChoiceOption(
        string adventureId,
        string choiceId,
        string optionId,
        string? encounterId = null)
    {
        var adventure = GetOrCreate(adventureId);
        var rule = new AdventureChoiceActionRule(choiceId, optionId, encounterId);
        _entries[adventure.AdventureId] = Rebuild(
            adventure,
            choiceActions: adventure.ChoiceActions.Append(rule));
        return this;
    }

    public AdventureActionAllowlist AllowChoiceOption(
        string adventureId,
        string choiceId,
        string optionId,
        string? encounterId = null)
        => AddChoiceOption(adventureId, choiceId, optionId, encounterId);

    public bool IsAdventureAllowed(string adventureId)
        => !string.IsNullOrWhiteSpace(adventureId)
            && _entries.ContainsKey(adventureId.Trim());

    public bool IsEncounterAllowed(string adventureId, string? encounterId)
        => TryGetEntry(adventureId, out var entry)
            && entry.AllowsEncounter(encounterId);

    public bool TryGetEncounterAction(
        string adventureId,
        string? encounterId,
        out AdventureEncounterAction action)
    {
        if (IsEncounterAllowed(adventureId, encounterId))
        {
            action = new AdventureEncounterAction(adventureId.Trim(), Normalize(encounterId));
            return true;
        }

        action = null!;
        return false;
    }

    public bool TryGetFightAction(
        string adventureId,
        string? encounterId,
        string? monsterId,
        int round,
        out AdventureFightAction action)
    {
        action = null!;
        if (!TryGetEntry(adventureId, out var entry)
            || !entry.AllowsEncounter(encounterId)
            || round < 0)
            return false;

        var candidate = entry.FightActions
            .Where(rule => string.Equals(rule.EncounterId, Normalize(encounterId), StringComparison.Ordinal)
                || rule.EncounterId is null)
            .Where(rule => rule.Round is null || rule.Round == round)
            .Where(rule => rule.MonsterId is null
                || string.Equals(rule.MonsterId, Normalize(monsterId), StringComparison.Ordinal))
            .OrderBy(rule => rule.EncounterId is null)
            .ThenBy(rule => rule.Round is null)
            .ThenBy(rule => rule.ActionId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (candidate is null) return false;
        action = new AdventureFightAction(
            adventureId.Trim(),
            candidate.ActionId,
            round,
            Normalize(encounterId),
            Normalize(monsterId));
        return true;
    }

    public bool TryGetFightAction(
        string adventureId,
        string? encounterId,
        int round,
        out AdventureFightAction action)
        => TryGetFightAction(adventureId, encounterId, null, round, out action);

    public bool TryGetChoiceAction(
        string adventureId,
        string choiceId,
        IReadOnlyList<string>? availableOptions,
        string? encounterId,
        out AdventureChoiceAction action)
    {
        action = null!;
        if (!TryGetEntry(adventureId, out var entry)
            || !entry.AllowsEncounter(encounterId)
            || string.IsNullOrWhiteSpace(choiceId))
            return false;

        var available = new HashSet<string>(
            (availableOptions ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
            StringComparer.Ordinal);
        var candidate = entry.ChoiceActions
            .Where(rule => string.Equals(rule.ChoiceId, choiceId.Trim(), StringComparison.Ordinal))
            .Where(rule => string.Equals(rule.EncounterId, Normalize(encounterId), StringComparison.Ordinal)
                || rule.EncounterId is null)
            .OrderBy(rule => rule.EncounterId is null)
            .ThenBy(rule => rule.OptionId, StringComparer.Ordinal)
            .FirstOrDefault(rule => available.Count == 0 || available.Contains(rule.OptionId));

        if (candidate is null) return false;
        action = new AdventureChoiceAction(
            adventureId.Trim(),
            choiceId.Trim(),
            candidate.OptionId,
            Normalize(encounterId));
        return true;
    }

    public bool TryGetChoiceAction(
        string adventureId,
        string choiceId,
        IReadOnlyList<string>? availableOptions,
        out AdventureChoiceAction action)
        => TryGetChoiceAction(adventureId, choiceId, availableOptions, null, out action);

    private AdventureAllowlistEntry GetOrCreate(string adventureId)
    {
        var id = Require(adventureId, nameof(adventureId));
        if (_entries.TryGetValue(id, out var entry)) return entry;
        entry = new AdventureAllowlistEntry(id);
        _entries[id] = entry;
        return entry;
    }

    private bool TryGetEntry(string adventureId, out AdventureAllowlistEntry entry)
        => _entries.TryGetValue(adventureId?.Trim() ?? string.Empty, out entry!);

    private AdventureActionAllowlist Clone()
        => new(_entries.Values) { WaitForManualChoice = WaitForManualChoice };

    private static AdventureAllowlistEntry Rebuild(
        AdventureAllowlistEntry source,
        IEnumerable<string>? encounterIds = null,
        IEnumerable<AdventureFightActionRule>? fightActions = null,
        IEnumerable<AdventureChoiceActionRule>? choiceActions = null)
        => new(
            source.AdventureId,
            encounterIds ?? source.EncounterIds,
            fightActions ?? source.FightActions,
            choiceActions ?? source.ChoiceActions);

    private static string Require(string? value, string parameter)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An id is required.", parameter)
            : value.Trim();

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
