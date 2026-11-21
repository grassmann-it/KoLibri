namespace KolArbitrage.Core.Spiel;

/// <summary>Actions that the current fight page explicitly offers.</summary>
public enum CombatActionKind
{
    Unknown = 0,
    Attack = 1,
    Skill = 2,
    Item = 3,
    Run = 4,
    UseItem = Item,
    RunAway = Run,
    Flee = Run
}

/// <summary>Current state of a fight response.</summary>
public enum CombatOutcome
{
    Unknown = 0,
    Ongoing = 1,
    InProgress = Ongoing,
    Victory = 2,
    Won = Victory,
    Defeat = 3,
    Lost = Defeat,
    Fled = 4,
    Runaway = Fled
}

/// <summary>A legal action represented by a control on a fight page.</summary>
public sealed record CombatAction
{
    public CombatActionKind Kind { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Value { get; init; }
    public int? MpCost { get; init; }
    public bool IsEnabled { get; init; } = true;

    public CombatAction()
    {
    }

    public CombatAction(CombatActionKind kind, string? id = null, string? name = null,
        int? mpCost = null)
    {
        Kind = kind;
        Id = NullIfWhiteSpace(id);
        Name = NullIfWhiteSpace(name);
        MpCost = mpCost;
        Value = Id;
    }

    public CombatActionKind ActionKind => Kind;
    public CombatActionKind Type => Kind;
    public string? ActionId => Id;
    public string? SkillId => Kind == CombatActionKind.Skill ? Id : null;
    public string? ItemId => Kind == CombatActionKind.Item ? Id : null;
    public string? Label => Name;
    public int? ManaCost => MpCost;

    internal string? AdventureActionId => Kind switch
    {
        CombatActionKind.Attack => "attack",
        CombatActionKind.Run => "run",
        CombatActionKind.Skill when Id is not null => $"skill:{Id}",
        CombatActionKind.Item when Id is not null => $"item:{Id}",
        _ => null
    };

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>An effect visible on the fight response (buff, debuff, or combat status).</summary>
public sealed record CombatEffect
{
    public string Name { get; init; } = string.Empty;
    public string? Id { get; init; }
    public int? Duration { get; init; }
    public string? Description { get; init; }

    public CombatEffect()
    {
    }

    public CombatEffect(string name, int? duration = null, string? id = null,
        string? description = null)
    {
        Name = name?.Trim() ?? string.Empty;
        Duration = duration;
        Id = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public string EffectName => Name;
    public int? TurnsRemaining => Duration;
}

/// <summary>Optional details for a monster parsed from a fight page.</summary>
public sealed record CombatMonster
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public int? Hp { get; init; }
    public int? MaxHp { get; init; }
    public int? Mp { get; init; }
    public int? MaxMp { get; init; }

    public CombatMonster()
    {
    }

    public CombatMonster(string? name, string? id = null, int? hp = null, int? maxHp = null,
        int? mp = null, int? maxMp = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Id = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        Hp = hp;
        MaxHp = maxHp;
        Mp = mp;
        MaxMp = maxMp;
    }

    public int? CurrentHp => Hp;
    public int? CurrentMp => Mp;
}

/// <summary>A current/max resource pair. Unknown values remain null.</summary>
public readonly record struct CombatResource(int? Current, int? Maximum)
{
    public int? CurrentValue => Current;
    public int? Max => Maximum;
    public int? MaxValue => Maximum;
    public bool IsKnown => Current is not null;
}

/// <summary>
/// Read-only state extracted from one KoL fight response. This model does not
/// submit actions; it is safe to use as input to a policy or a manual preview.
/// </summary>
public sealed record CombatObservation
{
    public string? Monster { get; init; }
    public string? MonsterName { get; init; }
    public string? MonsterId { get; init; }
    public CombatMonster? MonsterDetails { get; init; }

    public int Round { get; init; }

    public int? PlayerHp { get; init; }
    public int? PlayerMaxHp { get; init; }
    public int? PlayerMp { get; init; }
    public int? PlayerMaxMp { get; init; }

    public int? EnemyHp { get; init; }
    public int? EnemyMaxHp { get; init; }
    public int? EnemyMp { get; init; }
    public int? EnemyMaxMp { get; init; }

    public IReadOnlyList<CombatAction> AvailableActions { get; init; }
        = Array.Empty<CombatAction>();
    public IReadOnlyList<CombatEffect> Effects { get; init; }
        = Array.Empty<CombatEffect>();
    public string? Familiar { get; init; }
    public CombatOutcome Outcome { get; init; } = CombatOutcome.Unknown;
    public string ResponseHash { get; init; } = string.Empty;
    public string? SourcePath { get; init; }
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();

    public CombatObservation()
    {
    }

    /// <summary>Convenience constructor for fixture and policy code.</summary>
    public CombatObservation(
        string? monster,
        int round,
        int? playerHp = null,
        int? playerMp = null,
        int? enemyHp = null,
        int? enemyMp = null,
        IReadOnlyList<CombatAction>? availableActions = null,
        IReadOnlyList<CombatEffect>? effects = null,
        CombatOutcome outcome = CombatOutcome.Unknown,
        string? responseHash = null)
    {
        Monster = string.IsNullOrWhiteSpace(monster) ? null : monster.Trim();
        MonsterName = Monster;
        Round = Math.Max(0, round);
        PlayerHp = playerHp;
        PlayerMp = playerMp;
        EnemyHp = enemyHp;
        EnemyMp = enemyMp;
        AvailableActions = availableActions ?? Array.Empty<CombatAction>();
        Effects = effects ?? Array.Empty<CombatEffect>();
        Outcome = outcome;
        ResponseHash = responseHash ?? string.Empty;
    }

    public int FightRound => Round;
    public int CurrentRound => Round;
    public int? PlayerHP => PlayerHp;
    public int? PlayerMP => PlayerMp;
    public int? EnemyHP => EnemyHp;
    public int? EnemyMP => EnemyMp;
    public int? MonsterHp => EnemyHp;
    public int? MonsterMaxHp => EnemyMaxHp;
    public int? MonsterMp => EnemyMp;
    public int? MonsterMaxMp => EnemyMaxMp;
    public int? CurrentPlayerHp => PlayerHp;
    public int? CurrentPlayerMp => PlayerMp;
    public int? CurrentEnemyHp => EnemyHp;
    public int? CurrentEnemyMp => EnemyMp;
    public IReadOnlyList<CombatAction> LegalActions => AvailableActions;
    public string Hash => ResponseHash;
    public string Sha256 => ResponseHash;
    public string ResponseSha256 => ResponseHash;
    public CombatOutcome OutcomeKind => Outcome;
    public bool IsInProgress => Outcome == CombatOutcome.Ongoing;
    public bool IsTerminal => Outcome is CombatOutcome.Victory or CombatOutcome.Defeat or CombatOutcome.Fled;
    public bool HasKnownMonster => !string.IsNullOrWhiteSpace(MonsterName);
    public bool CanContinue => Outcome == CombatOutcome.Ongoing && AvailableActions.Count > 0;

    public CombatResource PlayerHealth => new(PlayerHp, PlayerMaxHp);
    public CombatResource PlayerMana => new(PlayerMp, PlayerMaxMp);
    public CombatResource EnemyHealth => new(EnemyHp, EnemyMaxHp);
    public CombatResource EnemyMana => new(EnemyMp, EnemyMaxMp);
    public CombatMonster Enemy => MonsterDetails
        ?? new CombatMonster(MonsterName, MonsterId, EnemyHp, EnemyMaxHp, EnemyMp, EnemyMaxMp);

    public CombatObservation Normalize()
        => this with
        {
            Monster = NullIfWhiteSpace(Monster ?? MonsterName),
            MonsterName = NullIfWhiteSpace(MonsterName ?? Monster),
            MonsterId = NullIfWhiteSpace(MonsterId),
            Round = Math.Max(0, Round),
            PlayerHp = NonNegative(PlayerHp),
            PlayerMaxHp = NonNegative(PlayerMaxHp),
            PlayerMp = NonNegative(PlayerMp),
            PlayerMaxMp = NonNegative(PlayerMaxMp),
            EnemyHp = NonNegative(EnemyHp),
            EnemyMaxHp = NonNegative(EnemyMaxHp),
            EnemyMp = NonNegative(EnemyMp),
            EnemyMaxMp = NonNegative(EnemyMaxMp),
            AvailableActions = AvailableActions ?? Array.Empty<CombatAction>(),
            Effects = Effects ?? Array.Empty<CombatEffect>(),
            Advisories = Advisories ?? Array.Empty<string>(),
            ResponseHash = ResponseHash ?? string.Empty
        };

    private static int? NonNegative(int? value) => value is null ? null : Math.Max(0, value.Value);
    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
