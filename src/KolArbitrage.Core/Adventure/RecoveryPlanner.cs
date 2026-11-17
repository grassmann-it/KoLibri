namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Builds a deterministic, bounded recovery preview from a read-only account
/// snapshot.  The planner never calls KoL and never emits an HTTP request.
/// </summary>
public sealed class RecoveryPlanner
{
    private readonly RecoveryPolicy _defaults;
    private readonly TimeProvider _clock;

    public RecoveryPlanner(RecoveryPolicy? policy = null, TimeProvider? clock = null)
    {
        _defaults = (policy ?? new RecoveryPolicy()).Normalize();
        _clock = clock ?? TimeProvider.System;
    }

    public RecoveryPolicy Policy => _defaults;
    public RecoveryPolicy Options => _defaults;
    public RecoveryPolicy DefaultPolicy => _defaults;

    public RecoveryPlan Plan(RecoverySnapshot snapshot, RecoveryPolicy? policy = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var settings = policy is null || ReferenceEquals(policy, _defaults)
            ? _defaults
            : policy.Normalize();
        var value = snapshot.Normalize();
        var timestamp = now ?? _clock.GetUtcNow();
        var steps = new List<RecoveryPlanStep>();
        var reasons = new List<RecoveryStopReason>();
        var advisories = new List<string>();
        var projected = new Projection(value);

        // A recovery preview can be shown for stale data, but stale data never
        // passes the combat gate.  This distinction lets a UI explain what it
        // would do while preserving the no-POST safety boundary.
        if (settings.RequireFreshSnapshot && value.IsStale)
        {
            reasons.Add(RecoveryStopReason.StaleSnapshot);
            advisories.Add("The recovery snapshot is stale; refresh account state before combat.");
        }

        if (value.MaximumHp <= 0)
        {
            reasons.Add(RecoveryStopReason.InvalidSnapshot);
            advisories.Add("Maximum HP is missing or zero; combat entry is blocked.");
        }
        if (value.CurrentHp > value.MaximumHp || value.CurrentMp > Math.Max(0, value.MaximumMp))
        {
            reasons.Add(RecoveryStopReason.InvalidSnapshot);
            advisories.Add("Current HP/MP exceeds the observed maximum; refresh the snapshot before combat.");
        }

        var hpTarget = Target(value.MaximumHp, settings.MinimumHp, settings.MinimumHpPercent);
        var mpTarget = Target(value.MaximumMp, settings.MinimumMp, settings.MinimumMpPercent);

        PlanFamiliarAndEquipment(value, settings, projected, steps, reasons, advisories);
        PlanEffects(value, settings, projected, steps, reasons, advisories);
        PlanHp(value, settings, projected, hpTarget, steps, reasons, advisories);
        PlanMp(value, settings, projected, mpTarget, steps, reasons, advisories);

        var currentlySafe = CanEnterCombat(value, settings, out var gateReasons);
        foreach (var reason in gateReasons)
            AddReason(reasons, reason);

        var projectedSafe = projected.CanSatisfy(value, settings, hpTarget, mpTarget, out var projectedReasons);
        var actionSteps = steps.Where(x => x.Action != RecoveryActionKind.Stop).ToArray();
        if (!currentlySafe && reasons.Count == 0 && actionSteps.Length > 0)
            advisories.Add("Recovery steps are available; refresh and re-evaluate before entering combat.");
        foreach (var reason in projectedReasons)
        {
            // Current-state reasons already explain the stop. Keep the
            // projected list in advisories so callers can distinguish a
            // missing resource from a not-yet-observed effect.
            if (!reasons.Contains(reason))
                advisories.Add($"Projected recovery remains incomplete: {reason}.");
        }

        var hasHardStop = reasons.Any(x => x is not RecoveryStopReason.None
            and not RecoveryStopReason.HpBelowMinimum
            and not RecoveryStopReason.MpBelowMinimum);
        var status = hasHardStop
            ? RecoveryPlanStatus.Blocked
            : currentlySafe
                ? RecoveryPlanStatus.Ready
                : actionSteps.Length > 0 && projectedSafe
                    ? RecoveryPlanStatus.RecoveryRequired
                    : RecoveryPlanStatus.Blocked;

        if (settings.EmitStopStep && hasHardStop && steps.Count < settings.MaxSteps)
        {
            steps.Add(new RecoveryPlanStep
            {
                Sequence = steps.Count + 1,
                Action = RecoveryActionKind.Stop,
                Reason = BuildStopReason(reasons),
                RequiresFreshState = true
            });
        }

        // A zero-step policy is itself a useful bounded result. Do not exceed
        // the configured cap even when a stop marker would be helpful.
        if (steps.Count > settings.MaxSteps)
            steps = steps.Take(settings.MaxSteps).ToList();

        return new RecoveryPlan
        {
            GeneratedAt = timestamp,
            Status = status,
            CanEnterCombat = currentlySafe,
            ProjectedCanEnterCombat = projectedSafe,
            Steps = steps,
            StopReasons = reasons.Distinct().ToArray(),
            Advisories = advisories.Distinct(StringComparer.Ordinal).ToArray()
        }.Normalize();
    }

    public RecoveryPlan Plan(RecoverySnapshot snapshot, DateTimeOffset? now)
        => Plan(snapshot, null, now);

    public RecoveryPlan Plan(RecoveryPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Plan(request.Snapshot, request.Policy, request.AsOf);
    }

    public RecoveryPlan Plan(RecoveryPlanRequest request, RecoveryPolicy? policy,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Plan(request.Snapshot, policy ?? request.Policy, now ?? request.AsOf);
    }

    public RecoveryPlan Build(RecoverySnapshot snapshot, RecoveryPolicy? policy = null,
        DateTimeOffset? now = null)
        => Plan(snapshot, policy, now);

    public RecoveryPlan Preview(RecoverySnapshot snapshot, RecoveryPolicy? policy = null,
        DateTimeOffset? now = null)
        => Plan(snapshot, policy, now);

    public RecoveryPlan Evaluate(RecoverySnapshot snapshot, RecoveryPolicy? policy = null,
        DateTimeOffset? now = null)
        => Plan(snapshot, policy, now);

    public RecoveryPlan BuildPlan(RecoverySnapshot snapshot, RecoveryPolicy? policy = null,
        DateTimeOffset? now = null)
        => Plan(snapshot, policy, now);

    public RecoveryPlan CreatePlan(RecoverySnapshot snapshot, RecoveryPolicy? policy = null,
        DateTimeOffset? now = null)
        => Plan(snapshot, policy, now);

    /// <summary>Returns true only when the current snapshot itself is combat-safe.</summary>
    public bool CanEnterCombat(RecoverySnapshot snapshot, RecoveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var settings = policy is null || ReferenceEquals(policy, _defaults)
            ? _defaults
            : policy.Normalize();
        return CanEnterCombat(snapshot.Normalize(), settings, out _);
    }

    public bool CanEnterCombat(RecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.CanEnterCombat;
    }

    public bool ShouldStop(RecoverySnapshot snapshot, RecoveryPolicy? policy = null)
        => !CanEnterCombat(snapshot, policy);

    public RecoveryStopReason StopReason(RecoverySnapshot snapshot, RecoveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var settings = policy is null || ReferenceEquals(policy, _defaults)
            ? _defaults
            : policy.Normalize();
        _ = CanEnterCombat(snapshot.Normalize(), settings, out var reasons);
        return reasons.FirstOrDefault();
    }

    public IReadOnlyList<RecoveryStopReason> StopReasons(RecoverySnapshot snapshot,
        RecoveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var settings = policy is null || ReferenceEquals(policy, _defaults)
            ? _defaults
            : policy.Normalize();
        _ = CanEnterCombat(snapshot.Normalize(), settings, out var reasons);
        return reasons;
    }

    private static void PlanFamiliarAndEquipment(
        RecoverySnapshot snapshot,
        RecoveryPolicy policy,
        Projection projected,
        ICollection<RecoveryPlanStep> steps,
        ICollection<RecoveryStopReason> reasons,
        ICollection<string> advisories)
    {
        if (!string.IsNullOrWhiteSpace(policy.RequiredFamiliarId)
            && !Same(snapshot.FamiliarId, policy.RequiredFamiliarId))
        {
            var id = policy.RequiredFamiliarId.Trim();
            if (!AddStep(steps, policy, RecoveryActionKind.SelectFamiliar,
                id, null, null, 0, 0,
                $"Select familiar '{id}' before combat.", advisories))
            {
                AddReason(reasons, RecoveryStopReason.PlanStepLimit);
            }
            else
            {
                projected.FamiliarId = id;
            }
        }

        foreach (var requirement in policy.RequiredEquipment
                     .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var current = snapshot.Equipment.TryGetValue(requirement.Key, out var item)
                ? item
                : null;
            if (Same(current, requirement.Value)) continue;

            if (!AddStep(steps, policy, RecoveryActionKind.Equip,
                requirement.Value, null, requirement.Key, 0, 0,
                $"Equip '{requirement.Value}' in slot '{requirement.Key}' before combat.", advisories))
            {
                AddReason(reasons, RecoveryStopReason.PlanStepLimit);
                break;
            }

            projected.Equipment[requirement.Key] = requirement.Value;
        }
    }

    private static void PlanEffects(
        RecoverySnapshot snapshot,
        RecoveryPolicy policy,
        Projection projected,
        ICollection<RecoveryPlanStep> steps,
        ICollection<RecoveryStopReason> reasons,
        ICollection<string> advisories)
    {
        var requirements = policy.EffectRequirements
            .Select(x => (EffectId: x.EffectId, MinimumTurns: x.MinimumTurns))
            .Concat(policy.RequiredEffects.Select(x => (EffectId: x, MinimumTurns: 1)))
            .Concat(policy.RequiredEffectTurns.Select(x => (EffectId: x.Key, MinimumTurns: x.Value)))
            .Where(x => !string.IsNullOrWhiteSpace(x.EffectId))
            .GroupBy(x => x.EffectId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => (Id: x.Key, MinimumTurns: x.Max(y => y.MinimumTurns)))
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in requirements)
        {
            var turns = snapshot.EffectTurns.TryGetValue(requirement.Id, out var observed)
                ? observed
                : snapshot.Effects.Contains(requirement.Id, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
            if (turns >= requirement.MinimumTurns)
            {
                projected.EffectTurns[requirement.Id] = turns;
                continue;
            }

            AddReason(reasons, turns == 0
                ? RecoveryStopReason.MissingEffect
                : RecoveryStopReason.EffectTurnsTooLow);
            advisories.Add($"Required effect '{requirement.Id}' is not confirmed for {requirement.MinimumTurns} turn(s).");
        }
    }

    private static void PlanHp(
        RecoverySnapshot snapshot,
        RecoveryPolicy policy,
        Projection projected,
        int target,
        ICollection<RecoveryPlanStep> steps,
        ICollection<RecoveryStopReason> reasons,
        ICollection<string> advisories)
    {
        var need = Math.Max(0, target - projected.CurrentHp);
        if (need <= 0) return;
        if (!policy.AllowConsumables)
        {
            AddReason(reasons, RecoveryStopReason.MissingHpRecovery);
            advisories.Add("Recovery consumables are disabled by policy; the HP floor cannot be reached.");
            return;
        }
        var planned = 0;
        var items = UsableItems(policy, snapshot, forHp: true);
        while (need > 0 && planned < policy.MaxRecoveryActions)
        {
            var item = items.FirstOrDefault(x => x.Remaining > 0 && x.Item.HpRestore > 0);
            if (item is null) break;
            item.Remaining--;
            planned++;
            var delta = Math.Min(item.Item.HpRestore, need);
            projected.CurrentHp = SafeAdd(projected.CurrentHp, item.Item.HpRestore, snapshot.MaximumHp);
            need = Math.Max(0, target - projected.CurrentHp);
            if (!AddStep(steps, policy, RecoveryActionKind.UseItem,
                item.Item.ItemId, null, null, delta, 0,
                $"Use '{item.Item.ItemId}' to reach the HP floor.", advisories))
            {
                AddReason(reasons, RecoveryStopReason.PlanStepLimit);
                break;
            }
        }

        if (need > 0)
        {
            AddReason(reasons, planned >= policy.MaxRecoveryActions
                ? RecoveryStopReason.RecoveryActionLimit
                : RecoveryStopReason.MissingHpRecovery);
            advisories.Add("The HP floor cannot be confirmed with the bounded recovery item budget.");
        }
    }

    private static void PlanMp(
        RecoverySnapshot snapshot,
        RecoveryPolicy policy,
        Projection projected,
        int target,
        ICollection<RecoveryPlanStep> steps,
        ICollection<RecoveryStopReason> reasons,
        ICollection<string> advisories)
    {
        var need = Math.Max(0, target - projected.CurrentMp);
        if (need <= 0) return;
        if (!policy.AllowConsumables)
        {
            AddReason(reasons, RecoveryStopReason.MissingMpRecovery);
            advisories.Add("Recovery consumables are disabled by policy; the MP floor cannot be reached.");
            return;
        }
        var planned = 0;
        var items = UsableItems(policy, snapshot, forHp: false);
        while (need > 0 && planned < policy.MaxRecoveryActions)
        {
            var item = items.FirstOrDefault(x => x.Remaining > 0 && x.Item.MpRestore > 0);
            if (item is null) break;
            item.Remaining--;
            planned++;
            var delta = Math.Min(item.Item.MpRestore, need);
            projected.CurrentMp = SafeAdd(projected.CurrentMp, item.Item.MpRestore, snapshot.MaximumMp);
            need = Math.Max(0, target - projected.CurrentMp);
            if (!AddStep(steps, policy, RecoveryActionKind.UseItem,
                item.Item.ItemId, null, null, 0, delta,
                $"Use '{item.Item.ItemId}' to reach the MP floor.", advisories))
            {
                AddReason(reasons, RecoveryStopReason.PlanStepLimit);
                break;
            }
        }

        if (need > 0)
        {
            AddReason(reasons, planned >= policy.MaxRecoveryActions
                ? RecoveryStopReason.RecoveryActionLimit
                : RecoveryStopReason.MissingMpRecovery);
            advisories.Add("The MP floor cannot be confirmed with the bounded recovery item budget.");
        }
    }

    private static bool CanEnterCombat(
        RecoverySnapshot snapshot,
        RecoveryPolicy policy,
        out IReadOnlyList<RecoveryStopReason> reasons)
    {
        var list = new List<RecoveryStopReason>();
        if (policy.RequireFreshSnapshot && snapshot.IsStale)
            list.Add(RecoveryStopReason.StaleSnapshot);
        if (snapshot.MaximumHp <= 0 || snapshot.CurrentHp < 0 || snapshot.CurrentHp > snapshot.MaximumHp)
            list.Add(RecoveryStopReason.InvalidSnapshot);
        if (policy.RequireFamiliarObservation && string.IsNullOrWhiteSpace(snapshot.FamiliarId))
            list.Add(RecoveryStopReason.MissingFamiliar);
        if (policy.RequireEquipmentObservation && snapshot.Equipment.Count == 0)
            list.Add(RecoveryStopReason.MissingEquipment);

        var hpTarget = Target(snapshot.MaximumHp, policy.MinimumHp, policy.MinimumHpPercent);
        var mpTarget = Target(snapshot.MaximumMp, policy.MinimumMp, policy.MinimumMpPercent);
        if (snapshot.CurrentHp < hpTarget)
            list.Add(RecoveryStopReason.HpBelowMinimum);
        if (snapshot.CurrentMp < mpTarget)
            list.Add(RecoveryStopReason.MpBelowMinimum);

        foreach (var requirement in policy.EffectRequirements
                     .Select(x => (EffectId: x.EffectId, MinimumTurns: x.MinimumTurns))
                     .Concat(policy.RequiredEffects.Select(x => (EffectId: x, MinimumTurns: 1)))
                     .Concat(policy.RequiredEffectTurns.Select(x => (EffectId: x.Key, MinimumTurns: x.Value)))
                     .Where(x => !string.IsNullOrWhiteSpace(x.EffectId))
                     .GroupBy(x => x.EffectId.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Select(x => (Id: x.Key, MinimumTurns: x.Max(y => y.MinimumTurns))))
        {
            var turns = snapshot.EffectTurns.TryGetValue(requirement.Id, out var value)
                ? value
                : snapshot.Effects.Contains(requirement.Id, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
            if (turns == 0) list.Add(RecoveryStopReason.MissingEffect);
            else if (turns < requirement.MinimumTurns) list.Add(RecoveryStopReason.EffectTurnsTooLow);
        }

        if (!string.IsNullOrWhiteSpace(policy.RequiredFamiliarId)
            && !snapshot.FamiliarAvailable)
            list.Add(RecoveryStopReason.MissingFamiliar);
        else if (!string.IsNullOrWhiteSpace(policy.RequiredFamiliarId)
                 && !Same(snapshot.FamiliarId, policy.RequiredFamiliarId))
            list.Add(RecoveryStopReason.MissingFamiliar);

        foreach (var requirement in policy.RequiredEquipment)
        {
            if (!snapshot.Equipment.TryGetValue(requirement.Key, out var value)
                || !Same(value, requirement.Value))
                list.Add(RecoveryStopReason.MissingEquipment);
        }

        reasons = list.Distinct().ToArray();
        return reasons.Count == 0;
    }

    private static IEnumerable<AvailableItem> UsableItems(
        RecoveryPolicy policy,
        RecoverySnapshot snapshot,
        bool forHp)
    {
        var items = (forHp ? policy.HpRecoveryItems : policy.MpRecoveryItems)
            .Concat(policy.RecoveryItems)
            .Where(x => x is not null && x.IsAllowed)
            .GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(x => forHp ? x.HpRestore : x.MpRestore)
                .ThenBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
                .First())
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemId)
                && (forHp ? x.HpRestore : x.MpRestore) > 0)
            .Select(x =>
            {
                var available = x.AvailableQuantity;
                if (snapshot.ItemQuantities.TryGetValue(x.ItemId, out var observed))
                    available = observed;
                return new AvailableItem(x, Math.Max(0, available));
            })
            .Where(x => x.Remaining > 0)
            .OrderByDescending(x => forHp ? x.Item.HpRestore : x.Item.MpRestore)
            .ThenBy(x => x.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return items;
    }

    private static bool AddStep(
        ICollection<RecoveryPlanStep> steps,
        RecoveryPolicy policy,
        RecoveryActionKind action,
        string? itemOrId,
        string? effectId,
        string? slot,
        int expectedHp,
        int expectedMp,
        string reason,
        ICollection<string> advisories)
    {
        if (steps.Count >= policy.MaxSteps)
        {
            advisories.Add("The recovery plan reached its configured step bound.");
            return false;
        }

        steps.Add(new RecoveryPlanStep
        {
            Sequence = steps.Count + 1,
            Action = action,
            ItemId = action is RecoveryActionKind.UseItem or RecoveryActionKind.Equip ? itemOrId : null,
            FamiliarId = action == RecoveryActionKind.SelectFamiliar ? itemOrId : null,
            EffectId = effectId,
            Slot = slot,
            ExpectedHpDelta = Math.Max(0, expectedHp),
            ExpectedMpDelta = Math.Max(0, expectedMp),
            Reason = reason,
            RequiresFreshState = true
        });
        return true;
    }

    private static int Target(int maximum, int absolute, decimal percent)
    {
        if (maximum <= 0) return Math.Max(0, absolute);
        var percentage = (int)Math.Ceiling(maximum * Math.Clamp(percent, 0m, 1m));
        return Math.Clamp(Math.Max(0, Math.Max(absolute, percentage)), 0, maximum);
    }

    private static int SafeAdd(int value, int delta, int maximum)
    {
        var next = (long)value + Math.Max(0, delta);
        if (maximum > 0) next = Math.Min(next, maximum);
        return next >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, next);
    }

    private static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void AddReason(ICollection<RecoveryStopReason> reasons, RecoveryStopReason reason)
    {
        if (reason != RecoveryStopReason.None && !reasons.Contains(reason))
            reasons.Add(reason);
    }

    private static string BuildStopReason(IEnumerable<RecoveryStopReason> reasons)
        => string.Join(", ", reasons.Where(x => x != RecoveryStopReason.None));

    private sealed class AvailableItem
    {
        public AvailableItem(RecoveryConsumable item, int remaining)
        {
            Item = item;
            Remaining = remaining;
        }

        public RecoveryConsumable Item { get; }
        public int Remaining { get; set; }
    }

    private sealed class Projection
    {
        public Projection(RecoverySnapshot snapshot)
        {
            CurrentHp = snapshot.CurrentHp;
            CurrentMp = snapshot.CurrentMp;
            FamiliarId = snapshot.FamiliarId;
            Equipment = snapshot.Equipment.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            EffectTurns = snapshot.EffectTurns.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        }

        public int CurrentHp { get; set; }
        public int CurrentMp { get; set; }
        public string? FamiliarId { get; set; }
        public Dictionary<string, string> Equipment { get; }
        public Dictionary<string, int> EffectTurns { get; }

        public bool CanSatisfy(
            RecoverySnapshot snapshot,
            RecoveryPolicy policy,
            int hpTarget,
            int mpTarget,
            out IReadOnlyList<RecoveryStopReason> reasons)
        {
            var list = new List<RecoveryStopReason>();
            if (CurrentHp < hpTarget) list.Add(RecoveryStopReason.HpBelowMinimum);
            if (CurrentMp < mpTarget) list.Add(RecoveryStopReason.MpBelowMinimum);
            if (!string.IsNullOrWhiteSpace(policy.RequiredFamiliarId)
                && !Same(FamiliarId, policy.RequiredFamiliarId))
                list.Add(RecoveryStopReason.MissingFamiliar);
            foreach (var requirement in policy.RequiredEquipment)
            {
                if (!Equipment.TryGetValue(requirement.Key, out var item)
                    || !Same(item, requirement.Value))
                    list.Add(RecoveryStopReason.MissingEquipment);
            }
            foreach (var requirement in policy.EffectRequirements
                         .Select(x => (EffectId: x.EffectId, MinimumTurns: x.MinimumTurns))
                         .Concat(policy.RequiredEffects.Select(x => (EffectId: x, MinimumTurns: 1)))
                         .Concat(policy.RequiredEffectTurns.Select(x => (EffectId: x.Key, MinimumTurns: x.Value)))
                         .Where(x => !string.IsNullOrWhiteSpace(x.EffectId)))
            {
                if (!EffectTurns.TryGetValue(requirement.EffectId, out var turns)
                    || turns < requirement.MinimumTurns)
                    list.Add(turns == 0
                        ? RecoveryStopReason.MissingEffect
                        : RecoveryStopReason.EffectTurnsTooLow);
            }
            reasons = list.Distinct().ToArray();
            return reasons.Count == 0;
        }
    }
}

/// <summary>Convenience request object for hosts that prefer request-shaped APIs.</summary>
public sealed record RecoveryPlanRequest
{
    public RecoverySnapshot Snapshot { get; init; } = new();
    public RecoveryPolicy? Policy { get; init; }
    public DateTimeOffset? AsOf { get; init; }
}
