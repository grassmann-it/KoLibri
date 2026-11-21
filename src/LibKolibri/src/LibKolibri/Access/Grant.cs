namespace LibKolibri.Access;

/// <summary>
/// Limits attached to one right. A null value means "not limited by this dimension",
/// except <see cref="DailyBudget"/> on a write right, which is mandatory.
/// </summary>
public sealed record RightLimit
{
    /// <summary>Amount per KoL day in the unit of the right (meat, adventures, messages, ...).</summary>
    public long? DailyBudget { get; init; }
    /// <summary>Highest price per unit the client may pay or set.</summary>
    public long? MaxUnitPrice { get; init; }
    /// <summary>Meat that must remain on the account after the action.</summary>
    public long? MeatReserve { get; init; }
    /// <summary>Largest allowed price change in percent (shop.reprice).</summary>
    public int? MaxChangePercent { get; init; }
}

/// <summary>What the human approved for one AI client. The secret token is never stored.</summary>
public sealed record Grant
{
    public required string Id { get; init; }
    public required string ClientName { get; init; }
    public required string TokenHash { get; init; }
    /// <summary>Accounts (role names or display names) the token may act on.</summary>
    public required IReadOnlyList<string> Accounts { get; init; }
    public required IReadOnlyDictionary<string, RightLimit> Rights { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>Why a request was allowed or refused. Stable codes the AI can react to.</summary>
public enum AccessCode
{
    Allowed,
    UnknownToken,
    Revoked,
    Expired,
    UnknownOperation,
    RightNotGranted,
    AccountNotGranted,
    BudgetExhausted,
    PriceAboveLimit,
    ChangeAboveLimit,
    BelowMeatReserve,
    PlanInvalid,
}

/// <summary>Outcome of an authorization check, including a reservation when allowed.</summary>
public sealed record AccessDecision(AccessCode Code, string Message, UsageReservation? Reservation = null)
{
    public bool Allowed => Code == AccessCode.Allowed;
}

/// <summary>What the client wants to do.</summary>
public sealed record AccessRequest(
    string Operation,
    string? Account = null,
    long Amount = 0,
    long? UnitPrice = null,
    long? MeatAfter = null,
    int? ChangePercent = null);
