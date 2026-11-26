using KoLibri.Internal;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri
{
    public enum ConsumeKind { Eat, Drink, Chew }

    public sealed class CharacterActions
    {
        /// <summary>Items that stay in the inventory when used (cheap toaster, Infinite BACON machine).</summary>
        private static readonly IReadOnlySet<int> ReusableItemIds = new HashSet<int> { 637, 9025 };

        private readonly KolibriSession _session;

        internal CharacterActions(KolibriSession session) => _session = session;

        public async Task<Status> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            if (refresh) return await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await _session.ReadAsync(r => r.FetchStatusAsync(false, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Mapping.ToStatus(snapshot, _session.Client.Data);
        }

        public Task<ActionResult> UseAsync(ItemRef item, int quantity = 1, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
            var action = Guarded(GuardedActionKind.Use, $"use {quantity} x item {item.Id}", item, quantity);
            return _session.GuardAsync(action, async () =>
            {
                var actions = await OpenAsync(cancellationToken).ConfigureAwait(false);
                var start = await _session.ActAsync(() => actions.ReadAsync(cancellationToken)).ConfigureAwait(false);
                var reusable = ReusableItemIds.Contains(item.Id);
                var current = Count(start.Inventory, item.Id);
                var last = start;
                var used = 0;
                string? rejection = null;
                try
                {
                    for (var i = 0; i < quantity; i++)
                    {
                        var result = await _session.ActAsync(() => actions.ExecuteAsync(new CoreSpiel.UseInventoryItem(item.Id), cancellationToken)).ConfigureAwait(false);
                        last = result;
                        if (result.RejectionReason is not null) { rejection = result.RejectionReason; break; }
                        // A reusable item keeps its stock: KoL accepting the request (no refusal text) is the evidence.
                        if (reusable && !result.Inventory.IsStale) { used++; continue; }
                        var after = result.Inventory.IsStale ? current : Count(result.Inventory, item.Id);
                        if (after >= current) { rejection = "KoL did not confirm the use."; break; }
                        used++;
                        current = after;
                    }
                }
                catch (CoreSpiel.CharacterActionOutcomeUnknownException unknown)
                {
                    return Unclear(unknown.Message, used, start);
                }
                return Result(used, quantity, rejection, start, last, ItemSource.Use);
            }, Succeeded, cancellationToken);
        }

        /// <summary>Prüft Organ-Platz vor dem Request; zu volle Organe ergeben ActionResult mit Rejection, keinen Request.</summary>
        public Task<ActionResult> ConsumeAsync(ItemRef item, ConsumeKind kind, int quantity = 1, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
            var action = Guarded(GuardedActionKind.Consume, $"{kind.ToString().ToLowerInvariant()} {quantity} x item {item.Id}", item, quantity);
            return _session.GuardAsync(action, async () =>
            {
                var actions = await OpenAsync(cancellationToken).ConfigureAwait(false);
                var start = await _session.ActAsync(() => actions.ReadAsync(cancellationToken)).ConfigureAwait(false);
                if (OrganRefusal(start.Status, item, kind, quantity) is { } full)
                    return new ActionResult(ActionStatus.Rejected, 0, full, null) { Before = ToStatus(start.Status), After = ToStatus(start.Status) };
                var before = Count(start.Inventory, item.Id);
                CoreSpiel.CharacterActionSnapshot result;
                try
                {
                    result = await _session.ActAsync(() => actions.ExecuteAsync(
                        new CoreSpiel.ConsumeCharacterItem(item.Id, CoreKind(kind), quantity), cancellationToken)).ConfigureAwait(false);
                }
                catch (CoreSpiel.CharacterActionOutcomeUnknownException unknown)
                {
                    return Unclear(unknown.Message, 0, start);
                }
                var after = result.Inventory.IsStale ? before : Count(result.Inventory, item.Id);
                var consumed = Math.Max(0, before - after);
                var rejection = result.RejectionReason ?? (consumed < quantity ? "KoL did not confirm all of it." : null);
                return Result(consumed, quantity, rejection, start, result, ItemSource.Use);
            }, Succeeded, cancellationToken);
        }

        public Task<ActionResult> CastAsync(SkillRef skill, int times = 1, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
            var action = Guarded(GuardedActionKind.Cast, $"cast skill {skill.Id} x{times}", null, times);
            return _session.GuardAsync(action, async () =>
            {
                var actions = await OpenAsync(cancellationToken).ConfigureAwait(false);
                // One cast per request, so the confirmed count is real.
                var outcome = await _session.ActAsync(() => actions.CastAsync(skill.Id, times, cancellationToken)).ConfigureAwait(false);
                return Result(outcome.ConfirmedCount, times, outcome.Problem, outcome.Before, outcome.After, ItemSource.Other);
            }, Succeeded, cancellationToken);
        }

        public Task<ActionResult> RestAsync(int times = 1, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
            var action = Guarded(GuardedActionKind.Rest, $"rest x{times}", null, times);
            return _session.GuardAsync(action, async () =>
            {
                var actions = await OpenAsync(cancellationToken).ConfigureAwait(false);
                var before = await _session.ActAsync(() => actions.ReadAsync(cancellationToken)).ConfigureAwait(false);
                var outcome = await _session.ActAsync(() => actions.RestAsync(times, cancellationToken)).ConfigureAwait(false);
                var done = outcome.Rests.Count;
                var after = await _session.ActAsync(() => actions.ReadAsync(cancellationToken)).ConfigureAwait(false);
                if (outcome.Unclear)
                    return new ActionResult(ActionStatus.Unclear, done, outcome.Problem, null) { Before = ToStatus(before.Status), After = ToStatus(after.Status) };
                return Result(done, times, outcome.Problem, before, after, ItemSource.Other);
            }, Succeeded, cancellationToken);
        }

        public Task<ActionResult> EquipAsync(ItemRef item, EquipSlot? slot = null, CancellationToken cancellationToken = default)
        {
            var target = slot ?? _session.Client.Data.Item(item)?.Slot
                ?? throw new ArgumentException($"The slot of item {item.Id} is unknown; pass it explicitly.", nameof(slot));
            return SimpleAsync(GuardedActionKind.Equip, $"equip item {item.Id} ({target})", item, async (actions, status) =>
                target == EquipSlot.Familiar
                    ? new CoreSpiel.EquipCharacterFamiliar(status.FamiliarId is { } id && int.TryParse(id, out var fam) ? fam
                        : throw new KolibriException("No familiar is active; nothing was equipped."), item.Id)
                    : new CoreSpiel.EquipInventoryItem(item.Id, Mapping.ToCoreSlot(target)), cancellationToken);
        }

        public Task<ActionResult> UnequipAsync(EquipSlot slot, CancellationToken cancellationToken = default)
            => SimpleAsync(GuardedActionKind.Equip, $"unequip {slot}", null, async (actions, status) =>
                slot == EquipSlot.Familiar
                    ? new CoreSpiel.EquipCharacterFamiliar(status.FamiliarId is { } id && int.TryParse(id, out var fam) ? fam
                        : throw new KolibriException("No familiar is active; nothing was changed."), null)
                    : new CoreSpiel.UnequipInventoryItem(Mapping.ToCoreSlot(slot)), cancellationToken);

        public Task<ActionResult> WearOutfitAsync(int outfitId, CancellationToken cancellationToken = default)
            => SimpleAsync(GuardedActionKind.Equip, $"wear outfit {outfitId}", null,
                (_, _) => Task.FromResult<CoreSpiel.CharacterAction>(new CoreSpiel.WearCharacterOutfit(outfitId)), cancellationToken);

        public Task<ActionResult> SetFamiliarAsync(int familiarId, CancellationToken cancellationToken = default)
            => SimpleAsync(GuardedActionKind.Equip, $"take familiar {familiarId}", null,
                (_, _) => Task.FromResult<CoreSpiel.CharacterAction>(new CoreSpiel.SelectCharacterFamiliar(familiarId)), cancellationToken);

        // ------------------------------------------------------------------ helpers

        private Task<ActionResult> SimpleAsync(GuardedActionKind kind, string summary, ItemRef? item,
            Func<CoreSpiel.CharacterActionClient, CoreSpiel.KoLStatusSnapshot, Task<CoreSpiel.CharacterAction>> build, CancellationToken cancellationToken)
        {
            var action = Guarded(kind, summary, item, 1);
            return _session.GuardAsync(action, async () =>
            {
                var actions = await OpenAsync(cancellationToken).ConfigureAwait(false);
                var before = await _session.ActAsync(() => actions.ReadAsync(cancellationToken)).ConfigureAwait(false);
                var request = await build(actions, before.Status).ConfigureAwait(false);
                try
                {
                    var after = await _session.ActAsync(() => actions.ExecuteAsync(request, cancellationToken)).ConfigureAwait(false);
                    return Result(after.RejectionReason is null ? 1 : 0, 1, after.RejectionReason, before, after, ItemSource.Other);
                }
                catch (CoreSpiel.CharacterActionOutcomeUnknownException unknown)
                {
                    return Unclear(unknown.Message, 0, before);
                }
            }, Succeeded, cancellationToken);
        }

        private async Task<CoreSpiel.CharacterActionClient> OpenAsync(CancellationToken cancellationToken)
        {
            var hash = await _session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
            return new CoreSpiel.CharacterActionClient(_session.Http, hash, _session.Client.Policy);
        }

        private GuardedAction Guarded(GuardedActionKind kind, string summary, ItemRef? item, int quantity)
            => new(_session.Account.Id, kind, summary) { Item = item, Quantity = quantity };

        private string? OrganRefusal(CoreSpiel.KoLStatusSnapshot raw, ItemRef item, ConsumeKind kind, int quantity)
        {
            var status = ToStatus(raw);
            var organ = kind switch { ConsumeKind.Eat => status.Fullness, ConsumeKind.Drink => status.Inebriety, _ => status.Spleen };
            var size = _session.Client.Data.Item(item)?.OrganSize ?? 0;
            if (organ.Limit is not { } limit || size <= 0) return null;
            var needed = size * quantity;
            if (organ.Current + needed <= limit) return null;
            var name = kind switch { ConsumeKind.Eat => "fullness", ConsumeKind.Drink => "inebriety", _ => "spleen" };
            return $"Not enough room: {name} {organ.Current}/{limit}, {quantity} x item {item.Id} needs {needed}. Nothing was sent.";
        }

        private static CoreSpiel.CharacterConsumableKind CoreKind(ConsumeKind kind) => kind switch
        {
            ConsumeKind.Eat => CoreSpiel.CharacterConsumableKind.Food,
            ConsumeKind.Drink => CoreSpiel.CharacterConsumableKind.Drink,
            ConsumeKind.Chew => CoreSpiel.CharacterConsumableKind.Spleen,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        private static int Count(CoreSpiel.KoLInventorySnapshot inventory, int id)
        {
            var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return inventory.Items.Where(x => x.ItemId == key).Sum(x => x.Quantity);
        }

        private Status ToStatus(CoreSpiel.KoLStatusSnapshot snapshot) => Mapping.ToStatus(snapshot, _session.Client.Data);

        private ActionResult Result(int completed, int requested, string? rejection,
            CoreSpiel.CharacterActionSnapshot before, CoreSpiel.CharacterActionSnapshot after, ItemSource source)
        {
            var status = completed >= requested && rejection is null ? ActionStatus.Done
                : completed > 0 ? ActionStatus.Partial
                : ActionStatus.Rejected;
            var beforeInventory = Mapping.ToInventory(before.Inventory);
            var afterInventory = after.Inventory.IsStale ? beforeInventory : Mapping.ToInventory(after.Inventory);
            var gained = Mapping.Gained(beforeInventory, afterInventory);
            var result = new ActionResult(status, completed, status == ActionStatus.Done ? null : rejection ?? "KoL did not confirm it.", null)
            {
                ItemsGained = gained,
                ItemsLost = Mapping.Lost(beforeInventory, afterInventory),
                Before = ToStatus(before.Status),
                After = after.Status.IsStale ? null : ToStatus(after.Status)
            };
            if (result.After is { } fresh) _session.RaiseStatus(result.Before, fresh);
            if (gained.Count > 0) _session.RaiseItems(gained, source);
            return result;
        }

        private ActionResult Unclear(string message, int completed, CoreSpiel.CharacterActionSnapshot before)
            => new(ActionStatus.Unclear, completed, message, null) { Before = ToStatus(before.Status) };

        private static bool Succeeded(ActionResult result) => result.Status is ActionStatus.Done or ActionStatus.Partial or ActionStatus.Unclear;
    }

    public enum ActionStatus { Done, Partial, Rejected, Unclear }

    /// <summary>Ergebnis einer verändernden Aktion, mit Status vorher/nachher, wo gelesen.</summary>
    public sealed record ActionResult(ActionStatus Status, int Completed, string? Rejection, Page? Page)
    {
        public IReadOnlyList<InventoryItem> ItemsGained { get; init; } = Array.Empty<InventoryItem>();
        public IReadOnlyList<InventoryItem> ItemsLost { get; init; } = Array.Empty<InventoryItem>();
        public Status? Before { get; init; }
        public Status? After { get; init; }
    }
}
