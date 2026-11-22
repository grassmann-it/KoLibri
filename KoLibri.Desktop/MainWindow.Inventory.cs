using System.Globalization;
using Avalonia.Controls;
using KoLibri.Core.Spiel;
using KoLibri.Desktop.ContextMenus;
using LibKolibri;

namespace KoLibri.Desktop;

/// <summary>Inventory-tab actions: eat/drink/chew/use with overfill warning, equip, and readable results.</summary>
public partial class MainWindow : IInventoryItemActions
{
    public ItemMenuTarget ToTarget(DesktopInventoryItem row) => _inventory.ToMenuTarget(row);

    public void Consume(ItemMenuTarget item, ItemUseKind kind, int quantity)
        => _ = RunInventoryConsumeAsync(item, kind, quantity);

    public void Equip(ItemMenuTarget item, string slot)
        => _ = RunInventoryEquipAsync(item, slot);

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
            try
            {
                using var session = await CharacterSession.OpenAsync(role, _accounts, token);
                var before = await session.ReadAsync(token);
                if (!ReferenceEquals(inventory, _inventory)) return;
                var state = new OrganState(before.Status.Fullness, before.Status.Inebriety, before.Status.Spleen);
                inventory.Organs = state;
                var check = InventoryActionPlanner.CheckOverfill(classification, quantity, state);
                if (check.Overfills)
                {
                    var ok = await OverfillConfirmDialog.AskAsync(this, "Overfill warning",
                        $"{check.Message}\n\n{state.Summary}\n\nContinue anyway?", $"{kind} anyway");
                    if (!ok)
                    {
                        ReportInventoryResult($"Cancelled: {check.Message}");
                        return;
                    }
                }

                CharacterActionSnapshot after = before;
                var done = 0;
                if (consumable is { } organKind)
                {
                    after = await session.ExecuteAsync(new ConsumeCharacterItem(id, organKind, quantity), token);
                    done = after.RejectionReason is null ? quantity : 0;
                }
                else
                {
                    for (; done < quantity; done++)
                    {
                        after = await session.ExecuteAsync(new UseInventoryItem(id), token);
                        if (after.RejectionReason is not null) break;
                    }
                }
                if (!ReferenceEquals(inventory, _inventory)) return;

                var projected = DesktopLiveDataProvider.ProjectInventory(role, after.Inventory, _catalog);
                inventory.ApplyLiveSnapshot(projected);
                var afterState = new OrganState(after.Status.Fullness, after.Status.Inebriety, after.Status.Spleen);
                inventory.Organs = afterState;
                var held = projected.Items.Where(r => r.ItemId == row.ItemId).Sum(r => r.Quantity);
                var changed = row.Quantity - held;
                if (after.RejectionReason is not null)
                    ReportInventoryResult($"{kind} {row.Name} failed: {after.RejectionReason}" + (done > 0 ? $" ({done} succeeded first)" : ""), error: true);
                else if (after.Inventory.IsStale || changed <= 0)
                    ReportInventoryResult($"KoL did not confirm {kind.ToString().ToLowerInvariant()}ing {row.Name} (you still hold {row.Quantity}). {afterState.Summary}", error: true);
                else
                    ReportInventoryResult($"{verb} {changed} × {row.Name}; {held} left. {afterState.Summary}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                ReportInventoryResult($"{kind} {row.Name} failed: {exception.Message}", error: true);
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
                using var session = await CharacterSession.OpenAsync(role, _accounts, token);
                var action = new EquipInventoryItem(id, slot);
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
