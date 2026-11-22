using System.Globalization;
using Avalonia.Controls;
using KoLibri.Core.Spiel;
using KoLibri.Desktop.ContextMenus;
using KoLibri.Core.Basis;
using LibKolibri;

namespace KoLibri.Desktop;

/// <summary>Inventory-tab actions: eat/drink/chew/use with overfill warning, equip, and readable results.</summary>
public partial class MainWindow : IInventoryItemActions
{
    /// <summary>Test seam: replaces the network handler of inventory-action sessions (never set in the app).</summary>
    public HttpMessageHandler? CharacterTransportOverride { get; set; }

    private Task<CharacterSession> OpenCharacterSessionAsync(AccountRole role, CancellationToken token)
        => CharacterSession.OpenAsync(role, _accounts, token, CharacterTransportOverride);

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

    private static int HeldOf(KoLInventorySnapshot inventory, int id)
        => inventory.Items.Where(i => i.ItemId == id.ToString(CultureInfo.InvariantCulture)).Sum(i => i.Quantity);

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
                using var session = await OpenCharacterSessionAsync(role, token);
                SetStatus($"{kind} {row.Name}: reading your status and inventory (KoL requests are paced, this takes a few seconds)…");
                var before = await session.ReadAsync(token);
                if (!ReferenceEquals(inventory, _inventory)) return;
                if (before.Inventory.IsStale || before.Status.IsStale)
                {
                    ReportInventoryResult($"{kind} {row.Name}: KoL did not return a fresh status and inventory, nothing was sent. Refresh and try again.", error: true);
                    return;
                }
                var heldBefore = HeldOf(before.Inventory, id);
                if (heldBefore < quantity)
                {
                    ReportInventoryResult($"{kind} {row.Name}: KoL shows {heldBefore} in your inventory now (the list said {row.Quantity}); nothing was sent. Refresh the list.", error: true);
                    return;
                }
                var state = new OrganState(before.Status.Fullness, before.Status.Inebriety, before.Status.Spleen);
                inventory.Organs = state;
                // Every organ cost of the item counts, also for plain "Use" items; an unknown cost or fill asks first.
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
                CharacterActionSnapshot after = before;
                string? stopReason = null;
                sent = true;
                if (consumable is { } organKind)
                {
                    after = await session.ExecuteAsync(new ConsumeCharacterItem(id, organKind, quantity), token);
                }
                else
                {
                    // One use at a time; each is confirmed before the next, a rejected one stops the series.
                    var previous = heldBefore;
                    for (var done = 0; done < quantity; done++)
                    {
                        after = await session.ExecuteAsync(new UseInventoryItem(id), token);
                        if (after.RejectionReason is not null) break;
                        if (after.Inventory.IsStale) break;
                        var now = HeldOf(after.Inventory, id);
                        if (!classification.IsReusable && now >= previous)
                        {
                            stopReason = $"KoL did not confirm use {done + 1} of {quantity}; the series was stopped.";
                            break;
                        }
                        previous = now;
                    }
                }
                if (!ReferenceEquals(inventory, _inventory)) return;

                var projected = DesktopLiveDataProvider.ProjectInventory(role, after.Inventory, _catalog);
                inventory.ApplyLiveSnapshot(projected);
                inventory.Organs = new OrganState(after.Status.Fullness, after.Status.Inebriety, after.Status.Spleen);
                var held = HeldOf(after.Inventory, id);
                var verdict = InventoryActionPlanner.JudgeConsume(verb, kind.ToString(), row.Name, quantity,
                    heldBefore, held, classification.IsReusable, after.RejectionReason,
                    after.Inventory.IsStale, stopReason);
                ReportInventoryResult($"{verdict.Message} {inventory.Organs.Summary}", error: !verdict.Ok);
            }
            catch (OperationCanceledException) { throw; }
            catch (CharacterActionOutcomeUnknownException exception)
            {
                ReportInventoryResult($"{kind} {row.Name}: {exception.Message}", error: true);
            }
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
        if (dualWield) slotName = nameof(CharacterEquipmentSlot.Offhand);
        if (!Enum.TryParse<CharacterEquipmentSlot>(slotName, out var slot))
        {
            ReportInventoryResult($"Unknown equipment slot '{slotName}'.", error: true);
            return;
        }
        var role = _selectedRole;
        var inventory = _inventory;
        await RunBusyOperationAsync($"Equip {row.Name}", async token =>
        {
            try
            {
                using var session = await OpenCharacterSessionAsync(role, token);
                var action = new EquipInventoryItem(id, slot, dualWield);
                var snapshot = await session.ExecuteAsync(action, token);
                if (!ReferenceEquals(inventory, _inventory)) return;
                inventory.ApplyLiveSnapshot(DesktopLiveDataProvider.ProjectInventory(role, snapshot.Inventory, _catalog));
                var worn = ObservedEquipment(snapshot.Status, slot) == id.ToString(CultureInfo.InvariantCulture);
                if (snapshot.RejectionReason is not null)
                    ReportInventoryResult($"Equip {row.Name} failed: {snapshot.RejectionReason}", error: true);
                else if (!worn)
                    ReportInventoryResult($"KoL did not confirm {row.Name} in {slot}; it may not meet the requirements.", error: true);
                else
                    ReportInventoryResult($"Equipped {row.Name} in {slot}.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                ReportInventoryResult($"Equip {row.Name} failed: {exception.Message}", error: true);
            }
        });
    }

    /// <summary>Item name for an id from the catalog, else "item #id" so a raw id is never shown bare.</summary>
    private string ItemNameOrId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "0") return "none";
        var name = _catalog?.Items.FirstOrDefault(record => record.Id == id)?.Name;
        return string.IsNullOrWhiteSpace(name) ? $"item #{id}" : name;
    }

    /// <summary>The catalog has no familiar table, so a familiar is shown as "familiar #id" (or "none").</summary>
    private static string FamiliarNameOrId(string? id)
        => string.IsNullOrWhiteSpace(id) || id == "0" ? "none" : $"#{id}";
}
