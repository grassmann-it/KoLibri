namespace KoLibri.Core.Spiel;

/// <summary>
/// A read-only effect observation.  Effects are represented separately from
/// the string ids on <see cref="RecoverySnapshot"/> so adapters that know
/// remaining turns can retain that information without making it mandatory.
/// </summary>
public sealed record RecoveryEffect
{
    public RecoveryEffect()
    {
    }

    public RecoveryEffect(string effectId, int turnsRemaining = 1, string? name = null)
    {
        EffectId = effectId?.Trim() ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? EffectId : name.Trim();
        TurnsRemaining = Math.Max(0, turnsRemaining);
    }

    public string EffectId { get; init; } = string.Empty;
    public string Id { get => EffectId; init => EffectId = value; }
    public string Name { get; init; } = string.Empty;
    public int TurnsRemaining { get; init; }
    public int Turns { get => TurnsRemaining; init => TurnsRemaining = value; }
    public bool IsActive => TurnsRemaining > 0;
}

/// <summary>Read-only current familiar observation.</summary>
public sealed record RecoveryFamiliar
{
    public RecoveryFamiliar()
    {
    }

    public RecoveryFamiliar(string familiarId, string? name = null, bool available = true)
    {
        FamiliarId = familiarId?.Trim() ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? FamiliarId : name.Trim();
        IsAvailable = available;
    }

    public string FamiliarId { get; init; } = string.Empty;
    public string Id { get => FamiliarId; init => FamiliarId = value; }
    public string Name { get; init; } = string.Empty;
    public bool IsAvailable { get; init; } = true;
    public bool Available { get => IsAvailable; init => IsAvailable = value; }
}

/// <summary>Read-only equipment slot observation.</summary>
public sealed record RecoveryEquipment
{
    public RecoveryEquipment()
    {
    }

    public RecoveryEquipment(string slot, string itemId, string? name = null)
    {
        Slot = slot?.Trim() ?? string.Empty;
        ItemId = itemId?.Trim() ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? ItemId : name.Trim();
    }

    public string Slot { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string Id { get => ItemId; init => ItemId = value; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// One permitted item use in a recovery preview.  The planner only consumes
/// this metadata; it never invokes an item endpoint.
/// </summary>
public record RecoveryConsumable
{
    public RecoveryConsumable()
    {
    }

    public RecoveryConsumable(
        string itemId,
        int hpRestore = 0,
        int mpRestore = 0,
        int availableQuantity = 0,
        string? name = null)
    {
        ItemId = itemId?.Trim() ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? ItemId : name.Trim();
        HpRestore = Math.Max(0, hpRestore);
        MpRestore = Math.Max(0, mpRestore);
        AvailableQuantity = Math.Max(0, availableQuantity);
    }

    public string ItemId { get; init; } = string.Empty;
    public string Id { get => ItemId; init => ItemId = value; }
    public string Name { get; init; } = string.Empty;
    public int HpRestore { get; init; }
    public int HitPointRestore { get => HpRestore; init => HpRestore = value; }
    public int Hp { get => HpRestore; init => HpRestore = value; }
    public int MpRestore { get; init; }
    public int ManaRestore { get => MpRestore; init => MpRestore = value; }
    public int Mp { get => MpRestore; init => MpRestore = value; }
    public int AvailableQuantity { get; init; }
    public int Quantity { get => AvailableQuantity; init => AvailableQuantity = value; }
    public bool IsAllowed { get; init; } = true;
}

/// <summary>Compatibility spelling for adapters that call a consumable an item.</summary>
public sealed record RecoveryItem : RecoveryConsumable
{
    public RecoveryItem()
    {
    }

    public RecoveryItem(
        string itemId,
        int hpRestore = 0,
        int mpRestore = 0,
        int availableQuantity = 0,
        string? name = null)
        : base(itemId, hpRestore, mpRestore, availableQuantity, name)
    {
    }
}

/// <summary>Compatibility spelling used by policy/options callers.</summary>
public sealed record RecoveryConsumableSpec : RecoveryConsumable
{
    public RecoveryConsumableSpec()
    {
    }

    public RecoveryConsumableSpec(
        string itemId,
        int hpRestore = 0,
        int mpRestore = 0,
        int availableQuantity = 0,
        string? name = null)
        : base(itemId, hpRestore, mpRestore, availableQuantity, name)
    {
    }
}

/// <summary>
/// Optional typed description of an effect that can be supplied by a host.
/// RecoveryPlanner remains conservative: an effect is only considered ready
/// after a fresh snapshot confirms it.
/// </summary>
public sealed record RecoveryEffectRequirement
{
    public RecoveryEffectRequirement()
    {
    }

    public RecoveryEffectRequirement(string effectId, int minimumTurns = 1)
    {
        EffectId = effectId?.Trim() ?? string.Empty;
        MinimumTurns = Math.Max(0, minimumTurns);
    }

    public string EffectId { get; init; } = string.Empty;
    public string Id { get => EffectId; init => EffectId = value; }
    public int MinimumTurns { get; init; } = 1;
    public int Turns { get => MinimumTurns; init => MinimumTurns = value; }
}

/// <summary>
/// Immutable account state used by the recovery planner.  It contains no
/// transport object and can safely be serialized or stored in a checkpoint.
/// </summary>
public sealed record RecoverySnapshot
{
    public RecoverySnapshot()
    {
    }

    public RecoverySnapshot(
        int currentHp,
        int maximumHp,
        int currentMp = 0,
        int maximumMp = 0,
        IEnumerable<string>? effects = null,
        string? familiarId = null,
        IReadOnlyDictionary<string, string>? equipment = null,
        IReadOnlyDictionary<string, int>? itemQuantities = null,
        bool isStale = false,
        DateTimeOffset? observedAt = null)
    {
        CurrentHp = currentHp;
        MaximumHp = maximumHp;
        CurrentMp = currentMp;
        MaximumMp = maximumMp;
        Effects = effects?.ToArray() ?? Array.Empty<string>();
        FamiliarId = familiarId;
        Equipment = equipment ?? EmptyEquipment;
        ItemQuantities = itemQuantities ?? EmptyItems;
        IsStale = isStale;
        ObservedAt = observedAt;
    }

    public int CurrentHp { get; init; }
    public int Hp { get => CurrentHp; init => CurrentHp = value; }
    public int HitPoints { get => CurrentHp; init => CurrentHp = value; }
    public int CurrentHitPoints { get => CurrentHp; init => CurrentHp = value; }

    public int MaximumHp { get; init; }
    public int MaxHp { get => MaximumHp; init => MaximumHp = value; }
    public int MaximumHitPoints { get => MaximumHp; init => MaximumHp = value; }
    public int MaxHitPoints { get => MaximumHp; init => MaximumHp = value; }
    public int HitPointsMaximum { get => MaximumHp; init => MaximumHp = value; }
    public int HitPointsCurrent { get => CurrentHp; init => CurrentHp = value; }

    public int CurrentMp { get; init; }
    public int Mp { get => CurrentMp; init => CurrentMp = value; }
    public int Mana { get => CurrentMp; init => CurrentMp = value; }
    public int CurrentMana { get => CurrentMp; init => CurrentMp = value; }

    public int MaximumMp { get; init; }
    public int MaxMp { get => MaximumMp; init => MaximumMp = value; }
    public int MaximumMana { get => MaximumMp; init => MaximumMp = value; }
    public int MaxMana { get => MaximumMp; init => MaximumMp = value; }
    public int ManaPointsMaximum { get => MaximumMp; init => MaximumMp = value; }
    public int ManaPointsCurrent { get => CurrentMp; init => CurrentMp = value; }

    /// <summary>Effect ids currently observed on the character.</summary>
    public IReadOnlyCollection<string> Effects { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ActiveEffects => Effects;
    public IReadOnlyCollection<string> EffectIds => Effects;

    /// <summary>Optional remaining-turn values keyed by effect id.</summary>
    public IReadOnlyDictionary<string, int> EffectTurns { get; init; } = EmptyEffectTurns;
    public IReadOnlyDictionary<string, int> EffectDurations => EffectTurns;
    public IReadOnlyDictionary<string, int> EffectTurnsById => EffectTurns;

    public string? FamiliarId { get; init; }
    public string? Familiar { get => FamiliarId; init => FamiliarId = value; }
    public string? CurrentFamiliarId { get => FamiliarId; init => FamiliarId = value; }
    public RecoveryFamiliar? FamiliarDetails { get; init; }
    public bool FamiliarAvailable { get; init; } = true;

    /// <summary>Currently equipped item id by slot (for example, weapon).</summary>
    public IReadOnlyDictionary<string, string> Equipment { get; init; } = EmptyEquipment;
    public IReadOnlyDictionary<string, string> EquippedItems => Equipment;
    public IReadOnlyDictionary<string, string> EquipmentBySlot => Equipment;
    public IReadOnlyList<RecoveryEquipment> EquipmentDetails { get; init; } = Array.Empty<RecoveryEquipment>();

    /// <summary>Known quantities of permitted recovery items.</summary>
    public IReadOnlyDictionary<string, int> ItemQuantities { get; init; } = EmptyItems;
    public IReadOnlyDictionary<string, int> Inventory => ItemQuantities;
    public IReadOnlyDictionary<string, int> Consumables => ItemQuantities;
    public IReadOnlyDictionary<string, int> ItemCounts => ItemQuantities;

    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? FetchedAt { get => ObservedAt; init => ObservedAt = value; }
    public bool IsStale { get; init; }
    public bool Stale { get => IsStale; init => IsStale = value; }
    public bool IsFresh => !IsStale;

    public decimal HpPercent => MaximumHp <= 0 ? 0m : Math.Clamp((decimal)CurrentHp / MaximumHp, 0m, 1m);
    public decimal MpPercent => MaximumMp <= 0 ? 0m : Math.Clamp((decimal)CurrentMp / MaximumMp, 0m, 1m);
    public decimal HealthPercent => HpPercent;
    public decimal ManaPercent => MpPercent;
    public decimal HpRatio => HpPercent;
    public decimal MpRatio => MpPercent;

    public static RecoverySnapshot FromEffects(
        int currentHp,
        int maximumHp,
        int currentMp,
        int maximumMp,
        IEnumerable<RecoveryEffect>? effects,
        string? familiarId = null,
        IReadOnlyDictionary<string, string>? equipment = null,
        IReadOnlyDictionary<string, int>? itemQuantities = null,
        bool isStale = false,
        DateTimeOffset? observedAt = null)
    {
        var rows = (effects ?? Array.Empty<RecoveryEffect>())
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.EffectId))
            .ToArray();
        return new RecoverySnapshot(currentHp, maximumHp, currentMp, maximumMp,
            rows.Select(x => x.EffectId), familiarId, equipment, itemQuantities, isStale, observedAt)
        {
            EffectTurns = rows
                .GroupBy(x => x.EffectId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => Math.Max(0, x.Max(y => y.TurnsRemaining)), StringComparer.OrdinalIgnoreCase)
        };
    }

    public static RecoverySnapshot From(CombatObservation observation,
        DateTimeOffset? observedAt = null, bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var value = observation.Normalize();
        return new RecoverySnapshot
        {
            CurrentHp = value.PlayerHp ?? 0,
            MaximumHp = value.PlayerMaxHp ?? value.PlayerHp ?? 0,
            CurrentMp = value.PlayerMp ?? 0,
            MaximumMp = value.PlayerMaxMp ?? value.PlayerMp ?? 0,
            Effects = value.Effects
                .Where(x => x is not null)
                .Select(x => x.Id ?? x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray(),
            EffectTurns = value.Effects
                .Where(x => x is not null)
                .Select(x => new { Id = x.Id ?? x.Name, Turns = x.Duration ?? 1 })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => Math.Max(0, x.Max(y => y.Turns)), StringComparer.OrdinalIgnoreCase),
            FamiliarId = value.Familiar,
            ObservedAt = observedAt,
            IsStale = isStale
        };
    }

    public static RecoverySnapshot FromCombatObservation(CombatObservation observation,
        DateTimeOffset? observedAt = null, bool isStale = false)
        => From(observation, observedAt, isStale);

    public RecoverySnapshot Normalize()
    {
        var effects = (Effects ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var effectTurns = NormalizeIntMap(EffectTurns);
        foreach (var effect in effects)
        {
            if (!effectTurns.ContainsKey(effect))
            {
                var copy = new Dictionary<string, int>(effectTurns, StringComparer.OrdinalIgnoreCase)
                {
                    [effect] = 1
                };
                effectTurns = copy;
            }
        }

        var equipment = NormalizeStringMap(Equipment);
        if (EquipmentDetails is { Count: > 0 })
        {
            foreach (var row in EquipmentDetails.Where(x => x is not null))
            {
                var slot = row.Slot?.Trim();
                var item = row.ItemId?.Trim();
                if (!string.IsNullOrWhiteSpace(slot) && !string.IsNullOrWhiteSpace(item))
                {
                    var copy = new Dictionary<string, string>(equipment, StringComparer.OrdinalIgnoreCase)
                    {
                        [slot] = item
                    };
                    equipment = copy;
                }
            }
        }

        var familiarId = FamiliarDetails?.FamiliarId ?? FamiliarId;
        return this with
        {
            CurrentHp = Math.Max(0, CurrentHp),
            MaximumHp = Math.Max(0, MaximumHp),
            CurrentMp = Math.Max(0, CurrentMp),
            MaximumMp = Math.Max(0, MaximumMp),
            Effects = effects,
            EffectTurns = effectTurns,
            FamiliarId = Normalize(familiarId),
            FamiliarDetails = FamiliarDetails is null
                ? null
                : FamiliarDetails with
                {
                    FamiliarId = Normalize(FamiliarDetails.FamiliarId) ?? string.Empty,
                    Name = Normalize(FamiliarDetails.Name) ?? Normalize(FamiliarDetails.FamiliarId) ?? string.Empty
                },
            Equipment = equipment,
            ItemQuantities = NormalizeIntMap(ItemQuantities)
        };
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyEffectTurns =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, string> EmptyEquipment =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, int> EmptyItems =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyDictionary<string, int> NormalizeIntMap(
        IReadOnlyDictionary<string, int>? source)
        => (source ?? EmptyEffectTurns)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value > 0)
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => Math.Max(0, x.Max(y => y.Value)), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> NormalizeStringMap(
        IReadOnlyDictionary<string, string>? source)
        => (source ?? EmptyEquipment)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Typed recovery policy; no request or commit capability is present.</summary>
public record RecoveryPolicy
{
    /// <summary>Absolute HP floor used by the combat gate.</summary>
    public int MinimumHp { get; init; } = 1;
    public int MinHp { get => MinimumHp; init => MinimumHp = value; }
    public int MinimumHitPoints { get => MinimumHp; init => MinimumHp = value; }
    public int MinimumHealth { get => MinimumHp; init => MinimumHp = value; }
    public int HpThreshold { get => MinimumHp; init => MinimumHp = value; }

    /// <summary>Optional relative HP floor, in the inclusive range 0..1.</summary>
    public decimal MinimumHpPercent { get; init; }
    public decimal MinHpPercent { get => MinimumHpPercent; init => MinimumHpPercent = value; }
    public decimal MinimumHealthPercent { get => MinimumHpPercent; init => MinimumHpPercent = value; }

    public int MinimumMp { get; init; }
    public int MinMp { get => MinimumMp; init => MinimumMp = value; }
    public int MinimumMana { get => MinimumMp; init => MinimumMp = value; }
    public int MinimumManaPoints { get => MinimumMp; init => MinimumMp = value; }
    public int MpThreshold { get => MinimumMp; init => MinimumMp = value; }
    public decimal MinimumMpPercent { get; init; }
    public decimal MinMpPercent { get => MinimumMpPercent; init => MinimumMpPercent = value; }
    public decimal MinimumManaPercent { get => MinimumMpPercent; init => MinimumMpPercent = value; }

    /// <summary>
    /// String ids are convenient for policies that only need presence. Turn
    /// requirements can be supplied through <see cref="EffectRequirements"/>.
    /// </summary>
    public IReadOnlyCollection<string> RequiredEffects { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Effects { get => RequiredEffects; init => RequiredEffects = value; }
    public IReadOnlyCollection<string> RequiredEffectIds => RequiredEffects;
    public IReadOnlyDictionary<string, int> RequiredEffectTurns { get; init; } = EmptyEffectTurns;
    public IReadOnlyDictionary<string, int> EffectTurns => RequiredEffectTurns;
    public IReadOnlyList<RecoveryEffectRequirement> EffectRequirements { get; init; } = Array.Empty<RecoveryEffectRequirement>();

    public string? RequiredFamiliarId { get; init; }
    public string? FamiliarId { get => RequiredFamiliarId; init => RequiredFamiliarId = value; }
    public string? RequiredFamiliar { get => RequiredFamiliarId; init => RequiredFamiliarId = value; }

    public IReadOnlyDictionary<string, string> RequiredEquipment { get; init; } = EmptyEquipment;
    public IReadOnlyDictionary<string, string> Equipment { get => RequiredEquipment; init => RequiredEquipment = value; }
    public IReadOnlyDictionary<string, string> RequiredEquippedItems => RequiredEquipment;

    /// <summary>Permitted HP and MP recovery items, in addition to the split lists.</summary>
    public IReadOnlyList<RecoveryConsumable> RecoveryItems { get; init; } = Array.Empty<RecoveryConsumable>();
    public IReadOnlyList<RecoveryConsumable> HpRecoveryItems { get; init; } = Array.Empty<RecoveryConsumable>();
    public IReadOnlyList<RecoveryConsumable> MpRecoveryItems { get; init; } = Array.Empty<RecoveryConsumable>();
    public IReadOnlyList<RecoveryConsumable> HpItems => HpRecoveryItems;
    public IReadOnlyList<RecoveryConsumable> MpItems => MpRecoveryItems;
    public IReadOnlyList<RecoveryConsumable> Consumables => RecoveryItems;

    /// <summary>Upper bound on emitted declarative steps.</summary>
    public int MaxSteps { get; init; } = 16;
    public int MaximumSteps { get => MaxSteps; init => MaxSteps = value; }
    public int MaxPlanSteps { get => MaxSteps; init => MaxSteps = value; }
    public int MaximumPlanSteps { get => MaxSteps; init => MaxSteps = value; }

    /// <summary>Upper bound on item-use steps; zero means no item can be used.</summary>
    public int MaxRecoveryActions { get; init; } = 16;
    public int MaxItemUses { get => MaxRecoveryActions; init => MaxRecoveryActions = value; }
    public int MaximumConsumables { get => MaxRecoveryActions; init => MaxRecoveryActions = value; }
    public int MaxPlanActions { get => MaxRecoveryActions; init => MaxRecoveryActions = value; }

    /// <summary>Stale data must never pass the combat gate by default.</summary>
    public bool RequireFreshSnapshot { get; init; } = true;
    public bool RequireFresh { get => RequireFreshSnapshot; init => RequireFreshSnapshot = value; }
    public bool AllowStaleSnapshot { get => !RequireFreshSnapshot; init => RequireFreshSnapshot = !value; }

    public bool AllowConsumables { get; init; } = true;
    public bool AllowRecoveryItems { get => AllowConsumables; init => AllowConsumables = value; }
    public bool RequireFamiliarObservation { get; init; }
    public bool RequireFamiliar { get => RequireFamiliarObservation; init => RequireFamiliarObservation = value; }
    public bool RequireEquipmentObservation { get; init; }
    public bool RequireEquipment { get => RequireEquipmentObservation; init => RequireEquipmentObservation = value; }

    /// <summary>When false, the planner reports unmet requirements but does not emit a Stop step.</summary>
    public bool EmitStopStep { get; init; } = true;
    public bool StopOnFailure { get => EmitStopStep; init => EmitStopStep = value; }

    public RecoveryPolicy Normalize()
    {
        Validate();
        var effects = (RequiredEffects ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requirements = (EffectRequirements ?? Array.Empty<RecoveryEffectRequirement>())
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.EffectId))
            .Select(x => x with
            {
                EffectId = x.EffectId.Trim(),
                MinimumTurns = Math.Max(0, x.MinimumTurns)
            })
            .OrderBy(x => x.EffectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var effectTurns = (RequiredEffectTurns ?? EmptyEffectTurns)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value >= 0)
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => Math.Max(0, x.Max(y => y.Value)), StringComparer.OrdinalIgnoreCase);
        var equipment = (RequiredEquipment ?? EmptyEquipment)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        var allItems = (RecoveryItems ?? Array.Empty<RecoveryConsumable>())
            .Concat(HpRecoveryItems ?? Array.Empty<RecoveryConsumable>())
            .Concat(MpRecoveryItems ?? Array.Empty<RecoveryConsumable>())
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.ItemId) && x.IsAllowed)
            .Select(x => x with
            {
                ItemId = x.ItemId.Trim(),
                Name = string.IsNullOrWhiteSpace(x.Name) ? x.ItemId.Trim() : x.Name.Trim(),
                HpRestore = Math.Max(0, x.HpRestore),
                MpRestore = Math.Max(0, x.MpRestore),
                AvailableQuantity = Math.Max(0, x.AvailableQuantity)
            })
            .GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(x => x.HpRestore + x.MpRestore)
                .ThenBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Keep split lists useful to callers while ensuring RecoveryItems can
        // be supplied as the one policy list.
        var hp = allItems.Where(x => x.HpRestore > 0).ToArray();
        var mp = allItems.Where(x => x.MpRestore > 0).ToArray();
        return this with
        {
            MinimumHp = Math.Max(0, MinimumHp),
            MinimumHpPercent = Math.Clamp(MinimumHpPercent, 0m, 1m),
            MinimumMp = Math.Max(0, MinimumMp),
            MinimumMpPercent = Math.Clamp(MinimumMpPercent, 0m, 1m),
            RequiredEffects = effects,
            RequiredEffectTurns = effectTurns,
            EffectRequirements = requirements,
            RequiredFamiliarId = Normalize(RequiredFamiliarId),
            RequiredEquipment = equipment,
            RecoveryItems = allItems,
            HpRecoveryItems = hp,
            MpRecoveryItems = mp,
            MaxSteps = Math.Max(0, MaxSteps),
            MaxRecoveryActions = Math.Max(0, MaxRecoveryActions)
        };
    }

    public void Validate()
    {
        if (MinimumHp < 0) throw new ArgumentOutOfRangeException(nameof(MinimumHp));
        if (MinimumMp < 0) throw new ArgumentOutOfRangeException(nameof(MinimumMp));
        if (MinimumHpPercent is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(MinimumHpPercent));
        if (MinimumMpPercent is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(MinimumMpPercent));
        if (MaxSteps < 0) throw new ArgumentOutOfRangeException(nameof(MaxSteps));
        if (MaxRecoveryActions < 0) throw new ArgumentOutOfRangeException(nameof(MaxRecoveryActions));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyEquipment =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, int> EmptyEffectTurns =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Action kind emitted by a recovery preview.</summary>
public enum RecoveryActionKind
{
    None = 0,
    UseItem = 1,
    UseConsumable = UseItem,
    HealHp = UseItem,
    HealMp = UseItem,
    Heal = UseItem,
    Equip = 2,
    EquipItem = Equip,
    SelectFamiliar = 3,
    ApplyEffect = 4,
    Stop = 5
}

/// <summary>Compatibility discriminator for serializers that use Type terminology.</summary>
public enum RecoveryActionType
{
    None = RecoveryActionKind.None,
    UseItem = RecoveryActionKind.UseItem,
    Equip = RecoveryActionKind.Equip,
    SelectFamiliar = RecoveryActionKind.SelectFamiliar,
    ApplyEffect = RecoveryActionKind.ApplyEffect,
    Stop = RecoveryActionKind.Stop
}

/// <summary>Reason a recovery preview cannot pass the combat gate.</summary>
public enum RecoveryStopReason
{
    None = 0,
    SnapshotMissing = 1,
    InvalidSnapshot = 2,
    StaleSnapshot = 3,
    HpBelowMinimum = 4,
    HitPointsBelowMinimum = HpBelowMinimum,
    MpBelowMinimum = 5,
    ManaBelowMinimum = MpBelowMinimum,
    MissingHpRecovery = 6,
    NoHpRecovery = MissingHpRecovery,
    MissingMpRecovery = 7,
    NoMpRecovery = MissingMpRecovery,
    RecoveryActionLimit = 8,
    MaxActionsReached = RecoveryActionLimit,
    PlanStepLimit = 9,
    MaxStepsReached = PlanStepLimit,
    MissingEffect = 10,
    EffectMissing = MissingEffect,
    EffectTurnsTooLow = 11,
    MissingFamiliar = 12,
    FamiliarMissing = MissingFamiliar,
    MissingEquipment = 13,
    EquipmentMissing = MissingEquipment,
    CombatNotAllowed = 14,
    Unknown = 99
}

/// <summary>Lifecycle of a bounded, read-only recovery preview.</summary>
public enum RecoveryPlanStatus
{
    Ready = 0,
    NoRecoveryNeeded = Ready,
    RecoveryRequired = 1,
    Blocked = 2,
    Stopped = Blocked,
    Bounded = 3
}

/// <summary>One declarative recovery step.  It contains no request body.</summary>
public record RecoveryPlanStep
{
    public int Sequence { get; init; }
    public RecoveryActionKind Action { get; init; }
    public RecoveryActionKind Kind { get => Action; init => Action = value; }
    public RecoveryActionType Type
    {
        get => (RecoveryActionType)Action;
        init => Action = (RecoveryActionKind)value;
    }
    public int Quantity { get; init; } = 1;
    public string? ItemId { get; init; }
    public string? EffectId { get; init; }
    public string? FamiliarId { get; init; }
    public string? Slot { get; init; }
    public int ExpectedHpDelta { get; init; }
    public int ExpectedMpDelta { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool RequiresFreshState { get; init; } = true;
    public bool IsPreviewOnly => true;
}

/// <summary>Short alias for callers that call a plan row a recovery step.</summary>
public record RecoveryStep : RecoveryPlanStep;

/// <summary>
/// Result of <see cref="RecoveryPlanner.Plan"/>.  A plan is always a preview;
/// callers must refresh the snapshot after each eventual mutation and call
/// <see cref="RecoveryPlanner.CanEnterCombat(RecoverySnapshot, RecoveryPolicy?)"/>
/// again before submitting a fight request.
/// </summary>
public sealed record RecoveryPlan
{
    public DateTimeOffset GeneratedAt { get; init; }
    public DateTimeOffset AsOf { get => GeneratedAt; init => GeneratedAt = value; }
    public RecoveryPlanStatus Status { get; init; }
    public bool CanEnterCombat { get; init; }
    public bool CanEnter => CanEnterCombat;
    public bool CanEnterCombatNow => CanEnterCombat;
    public bool ProjectedCanEnterCombat { get; init; }
    public bool RequiresRecovery => Steps.Any(x => x.Action != RecoveryActionKind.Stop);
    public bool ShouldStop => !CanEnterCombat || StopReasons.Count > 0;
    public bool Stop => ShouldStop;
    public bool IsStop => ShouldStop;
    public bool IsBlocked => Status is RecoveryPlanStatus.Blocked or RecoveryPlanStatus.Stopped;
    public bool IsPreviewOnly => true;
    public bool IsBounded => true;

    public IReadOnlyList<RecoveryPlanStep> Steps { get; init; } = Array.Empty<RecoveryPlanStep>();
    public IReadOnlyList<RecoveryPlanStep> Actions => Steps;
    public IReadOnlyList<RecoveryPlanStep> RecoverySteps => Steps;
    public IReadOnlyList<RecoveryStopReason> StopReasons { get; init; } = Array.Empty<RecoveryStopReason>();
    public IReadOnlyList<RecoveryStopReason> Reasons => StopReasons;
    public RecoveryStopReason StopReason => StopReasons.FirstOrDefault();
    public RecoveryStopReason PrimaryStopReason => StopReason;
    public bool HasStopReason => StopReasons.Count > 0;
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings => Advisories;
    public int StepCount => Steps.Count;

    public RecoveryPlan Normalize()
    {
        var steps = (Steps ?? Array.Empty<RecoveryPlanStep>())
            .Where(x => x is not null)
            .Select((x, index) => x with { Sequence = index + 1 })
            .ToArray();
        var reasons = (StopReasons ?? Array.Empty<RecoveryStopReason>())
            .Where(x => x != RecoveryStopReason.None)
            .Distinct()
            .ToArray();
        return this with
        {
            Steps = steps,
            StopReasons = reasons,
            Advisories = (Advisories ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
        };
    }
}
