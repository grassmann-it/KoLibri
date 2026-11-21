using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LibKolibri.Access;

/// <summary>A previewed write: valid once, only for the same operation and arguments.</summary>
public sealed record PlanTicket(string PlanId, string Operation, DateTimeOffset ExpiresAt);

/// <summary>
/// The single enforcement point for AI clients. Front ends (MCP) call
/// <see cref="Authorize"/> before an operation and book the result on the reservation;
/// humans using CLI or Desktop never pass through here.
/// </summary>
public sealed class AccessGate
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);
    private readonly GrantStore _grants;
    private readonly UsageLedger _ledger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, PlanEntry> _plans = new();

    private sealed record PlanEntry(string GrantId, string Operation, string ArgsHash, DateTimeOffset ExpiresAt);

    public AccessGate(GrantStore grants, UsageLedger ledger, TimeProvider? clock = null)
    {
        _grants = grants; _ledger = ledger; _clock = clock ?? TimeProvider.System;
    }

    public AccessDecision Authorize(string token, AccessRequest request)
    {
        var grant = _grants.FindByToken(token);
        if (grant is null) return Deny(AccessCode.UnknownToken, "Unknown token.");
        if (grant.RevokedAt is not null) return Deny(AccessCode.Revoked, "This access was revoked by the user.");
        if (grant.ExpiresAt <= _clock.GetUtcNow()) return Deny(AccessCode.Expired, "This access has expired. Ask the user for a new one.");

        var op = OperationCatalog.Find(request.Operation);
        if (op is null) return Deny(AccessCode.UnknownOperation, $"Unknown operation '{request.Operation}'.");
        if (!grant.Rights.TryGetValue(op.Right, out var limit))
            return Deny(AccessCode.RightNotGranted, $"Right '{op.Right}' was not granted. Request it again with the right included.");

        if (request.Account is not null && !grant.Accounts.Contains(request.Account, StringComparer.OrdinalIgnoreCase))
            return Deny(AccessCode.AccountNotGranted, $"Account '{request.Account}' is not part of this access.");

        if (op.Kind == OperationKind.Read) return new AccessDecision(AccessCode.Allowed, "Allowed.");

        if (limit.MaxUnitPrice is { } maxPrice && request.UnitPrice is { } price && price > maxPrice)
            return Deny(AccessCode.PriceAboveLimit, $"Unit price {price} is above the limit of {maxPrice}.");
        if (limit.MaxChangePercent is { } maxChange && request.ChangePercent is { } change && Math.Abs(change) > maxChange)
            return Deny(AccessCode.ChangeAboveLimit, $"Change of {change}% is above the limit of {maxChange}%.");
        if (limit.MeatReserve is { } reserve && request.MeatAfter is { } after && after < reserve)
            return Deny(AccessCode.BelowMeatReserve, $"The action would leave less than the reserve of {reserve} meat.");

        var reservation = _ledger.TryReserve(grant.Id, op.Right, op.Name, request.Account, request.Amount, limit.DailyBudget ?? 0);
        if (reservation is null)
        {
            var used = _ledger.UsedToday(grant.Id, op.Right);
            return Deny(AccessCode.BudgetExhausted,
                $"Daily budget for '{op.Right}' is used up ({used} of {limit.DailyBudget ?? 0} {op.BudgetUnit}). It resets at the KoL rollover.");
        }
        return new AccessDecision(AccessCode.Allowed, "Allowed.", reservation);
    }

    /// <summary>Remembers a previewed write and returns the id the client must send back to execute it.</summary>
    public PlanTicket IssuePlan(string token, string operation, string argumentsJson)
    {
        var grant = _grants.FindByToken(token) ?? throw new InvalidOperationException("Unknown token.");
        var plan = new PlanEntry(grant.Id, operation, Hash(argumentsJson), _clock.GetUtcNow() + PlanLifetime);
        var id = "plan_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        _plans[id] = plan;
        foreach (var stale in _plans.Where(p => p.Value.ExpiresAt <= _clock.GetUtcNow()).Select(p => p.Key).ToList())
            _plans.TryRemove(stale, out _);
        return new PlanTicket(id, operation, plan.ExpiresAt);
    }

    /// <summary>Consumes a plan: works once, for the same grant, operation and arguments, before it expires.</summary>
    public AccessDecision ConsumePlan(string token, string planId, string operation, string argumentsJson)
    {
        var grant = _grants.FindByToken(token);
        if (grant is null) return Deny(AccessCode.UnknownToken, "Unknown token.");
        if (!_plans.TryRemove(planId, out var plan))
            return Deny(AccessCode.PlanInvalid, "Plan is unknown or was already executed. Preview again.");
        if (plan.ExpiresAt <= _clock.GetUtcNow()) return Deny(AccessCode.PlanInvalid, "Plan expired. Preview again.");
        if (plan.GrantId != grant.Id || plan.Operation != operation || plan.ArgsHash != Hash(argumentsJson))
            return Deny(AccessCode.PlanInvalid, "Plan does not match this operation and arguments. Preview again.");
        return new AccessDecision(AccessCode.Allowed, "Plan accepted.");
    }

    private static AccessDecision Deny(AccessCode code, string message) => new(code, message);

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
