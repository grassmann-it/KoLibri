using System.Globalization;
using Avalonia.Controls;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>Inventory-tab actions: eat/drink/chew/use with overfill warning, equip, and readable results.</summary>
public partial class MainWindow : IInventoryItemActions
{
    /// <summary>The cached library session of a role (owned by <see cref="DesktopAccounts"/>, never disposed here).
    /// Offline tests replace the transport through <see cref="KolibriOptions.HttpHandler"/> of the accounts.</summary>
    private Task<KolibriSession> OpenCharacterSessionAsync(AccountRole role, CancellationToken token)
        => _live.RequireSessionAsync(role, "change the character", token);

    public ItemMenuTarget ToTarget(DesktopInventoryItem row) => _inventory.ToMenuTarget(row);

    public void Consume(ItemMenuTarget item, ItemUseKind kind, int quantity)
        => _ = GuardedAsync($"{kind} {item.Name}", () => RunInventoryConsumeAsync(item, kind, quantity));

    public void Equip(ItemMenuTarget item, string slot)
        => _ = GuardedAsync($"Equip {item.Name}", () => RunInventoryEquipAsync(item, slot));

    /// <summary>Runs an inventory action and turns any escaped exception into a visible message and a log entry
    /// (a fire-and-forget task or an async-void click handler would otherwise swallow or crash on it).</summary>
    internal async Task GuardedAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportInventoryResult($"{what} failed: {exception.Message}", error: true); }
    }

    /// <summary>Shows a result or error in the inventory tab, the status bar and the session log.</summary>
    private void ReportInventoryResult(string message, bool error = false)
    {
        _inventory.ActionResult = message;
        if (_inventoryView?.FindControl<TextBlock>("InventoryActionResult") is { } text)
        {
            text.Text = message;
            text.Classes.Set("error", error);
        }
        SetStatus(message);
        if (error) _log.Error(message); else _log.Add(SessionLogKind.Info, message);
    }

    private bool TryResolveRow(ItemMenuTarget item, int quantity, out DesktopInventoryItem row, out int id)
    {
        id = 0;
        row = _inventory.Items.FirstOrDefault(r => r.ItemId == item.ItemId)!;
        if (row is null || !int.TryParse(item.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
        {
            ReportInventoryResult($"{item.Name} is not in the loaded inventory; refresh and try again.", error: true);
            return false;
        }
        if (quantity < 1 || quantity > row.Quantity)
        {
            ReportInventoryResult($"Quantity {quantity} is not available: you hold {row.Quantity} × {row.Name}.", error: true);
            return false;
        }
        if (_refreshCancellation is not null)
        {
            ReportInventoryResult("Another operation is still running; wait for it to finish.", error: true);
            return false;
        }
        return true;
    }

    private static int HeldOf(Inventory inventory, int id) => inventory.Count(id);

    private async Task RunInventoryConsumeAsync(ItemMenuTarget item, ItemUseKind kind, int quantity)
    {
        if (!TryResolveRow(item, quantity, out var row, out var id)) return;
        var classification = _inventory.Classify(row);
        var verb = kind switch { ItemUseKind.Eat => "Ate", ItemUseKind.Drink => "Drank", ItemUseKind.Chew => "Chewed", _ => "Used" };
        var consumable = InventoryActionPlanner.ConsumableKindOf(classification.Organ);
        if (kind == ItemUseKind.None || (kind != ItemUseKind.Use) != (consumable is not null))
        {
            ReportInventoryResult($"{row.Name} cannot be {kind.ToString().ToLowerInvariant()}ed.", error: true);
            return;
        }

        var role = _selectedRole;
        var inventory = _inventory;
        await RunBusyOperationAsync($"{kind} {quantity} × {row.Name}", async token =>
        {
            var sent = false;
            try
            {
                var session = await OpenCharacterSessionAsync(role, token);
                SetStatus($"{kind} {row.Name}: reading your status and inventory (KoL requests are paced, this takes a few seconds)…");
                // 0.5: the library throws instead of returning old data, so a read that returns is fresh.
                var beforeStatus = await session.Character.GetStatusAsync(refresh: true, token);
                var beforeInventory = await session.Inventory.GetAsync(refresh: true, token);
                if (!ReferenceEquals(inventory, _inventory)) return;
                var heldBefore = HeldOf(beforeInventory, id);
                if (heldBefore < quantity)
                {
                    ReportInventoryResult($"{kind} {row.Name}: KoL shows {heldBefore} in your inventory now (the list said {row.Quantity}); nothing was sent. Refresh the list.", error: true);
                    return;
                }
                var state = OrganState.From(beforeStatus);
                inventory.Organs = state;
                // Every organ cost of the item counts, also for plain "Use" items; an unknown cost or fill asks first.
                // The library checks known organ limits again and refuses an overfill without sending it.
                var check = InventoryActionPlanner.CheckOverfill(classification, quantity, state);
                if (InventoryActionPlanner.NeedsConfirmation(check))
                {
                    var ok = await OverfillConfirmDialog.AskAsync(this,
                        check.Overfills ? "Overfill warning" : "Organ limit could not be checked",
                        $"{check.Message}\n\n{state.Summary}\n\nContinue anyway?", $"{kind} anyway");
                    if (!ok)
                    {
                        ReportInventoryResult($"Cancelled: {check.Message}");
                        return;
                    }
                }

                SetStatus($"{kind} {quantity} × {row.Name}: sending to KoL…");
                _log.Add(SessionLogKind.Info, $"{kind} {quantity} × {row.Name} (#{id}) sent to KoL.");
                sent = true;
                // Consume sends one request for the quantity; Use sends one use at a time, each confirmed before
                // the next, and a rejected one stops the series (both inside the library).
                var result = consumable is { } organKind
                    ? await session.Character.ConsumeAsync(id, organKind, quantity, token)
                    : await session.Character.UseAsync(id, quantity, token);
                if (!ReferenceEquals(inventory, _inventory)) return;

                var afterInventory = await session.Inventory.GetAsync(refresh: true, token);
                var afterStatus = result.After ?? await session.Character.GetStatusAsync(refresh: true, token);
                if (!ReferenceEquals(inventory, _inventory)) return;
                var projected = DesktopLiveDataProvider.ProjectInventory(role, afterInventory, _catalog);
                inventory.ApplyLiveSnapshot(projected);
                inventory.Organs = OrganState.From(afterStatus);
                var held = HeldOf(afterInventory, id);
                var rejection = DesktopCharacterActions.Rejection(result);
                var verdict = InventoryActionPlanner.JudgeConsume(verb, kind.ToString(), row.Name, quantity,
                    heldBefore, held, classification.IsReusable, rejection, stale: false);
                ReportInventoryResult($"{verdict.Message} {inventory.Organs.Summary}", error: !verdict.Ok);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                ReportInventoryResult(sent
                    ? $"{kind} {row.Name} failed after the request was sent, so it may have happened: {exception.Message} Refresh before repeating."
                    : $"{kind} {row.Name} failed: {exception.Message}", error: true);
            }
        });
    }

    private async Task RunInventoryEquipAsync(ItemMenuTarget item, string slotName)
    {
        if (!TryResolveRow(item, 1, out var row, out var id)) return;
        if (slotName == InventoryActionPlanner.FamiliarSlot)
        {
            if (TryReadCharacterId("CharacterFamiliarId", out var familiarId) && familiarId > 0)
                await RunFamiliarEquipmentAsync(id);
            else
                ReportInventoryResult($"Enter a familiar ID on the Gear page before equipping {row.Name} on a familiar.", error: true);
            return;
        }
        var dualWield = slotName == InventoryActionPlanner.DualWieldSlot;
        if (InventoryActionPlanner.EquipSlotOf(slotName) is not { } slot || slot == EquipSlot.Familiar)
        {
            ReportInventoryResult($"Unknown equipment slot '{slotName}'.", error: true);
            return;
        }
        var label = InventoryActionPlanner.SlotLabel(slot);
        var role = _selectedRole;
        var inventory = _inventory;
        await RunBusyOperationAsync($"Equip {row.Name}", async token =>
        {
            try
            {
                var session = await OpenCharacterSessionAsync(role, token);
                CharacterSnapshot snapshot;
                if (dualWield)
                    snapshot = await DesktopCharacterActions.ExecuteAsync(session, new DualWieldItem(id), token);
                else
                {
                    var result = await session.Character.EquipAsync(id, slot, token);
                    snapshot = await DesktopCharacterActions.ReadAsync(session, token)
                        with { RejectionReason = DesktopCharacterActions.Rejection(result) };
                }
                if (!ReferenceEquals(inventory, _inventory)) return;
                inventory.ApplyLiveSnapshot(DesktopLiveDataProvider.ProjectInventory(role, snapshot.Inventory, _catalog));
                var worn = DesktopCharacterActions.Worn(snapshot.Equipment, slot) == id;
                if (snapshot.RejectionReason is not null && !worn)
                    ReportInventoryResult($"Equip {row.Name} failed: {snapshot.RejectionReason}", error: true);
                else if (!worn)
                    ReportInventoryResult($"KoL did not confirm {row.Name} in {label}; it may not meet the requirements.", error: true);
                else
                    ReportInventoryResult($"Equipped {row.Name} in {label}.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                ReportInventoryResult($"Equip {row.Name} failed: {exception.Message}", error: true);
            }
        });
    }

    /// <summary>Item name for an id from the catalog, else "item #id" so a raw id is never shown bare.</summary>
    private string ItemNameOrId(int? id)
    {
        if (id is not { } value || value <= 0) return "none";
        var key = value.ToString(CultureInfo.InvariantCulture);
        var name = _catalog?.Items.FirstOrDefault(record => record.Id == key)?.Name
            ?? _live.Accounts.Client.Data.Item(value)?.Name;
        return string.IsNullOrWhiteSpace(name) ? $"item #{value}" : name;
    }

    /// <summary>The catalog has no familiar table, so a familiar is shown as "familiar #id" (or "none").</summary>
    private static string FamiliarNameOrId(int? id)
        => id is not { } value || value <= 0 ? "none" : $"#{value}";
}
