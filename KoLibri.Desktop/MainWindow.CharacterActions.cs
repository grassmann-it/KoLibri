using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LibKolibri;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

public sealed record CharacterStoredItemRow(int ItemId, bool InCloset, string Label);

public partial class MainWindow
{
    public async void OpenFriarsClick(object? sender, RoutedEventArgs e)
        => await RunFriarsAsync(null);

    public async void FriarRitualClick(object? sender, RoutedEventArgs e)
        => await RunFriarsAsync(new PerformFriarRitual());

    public async void FriarBlessingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && Enum.TryParse<FriarBlessing>(button.Tag?.ToString(), out var blessing))
            await RunFriarsAsync(new RequestFriarBlessing(blessing));
    }

    private Task RunFriarsAsync(CharacterAction? action)
    {
        if (action is not null && _encounterBrowser?.Session?.IsSessionActive == true)
        {
            SetStatus("Finish the current adventure before visiting a Friar.");
            return Task.CompletedTask;
        }
        var role = _selectedRole;
        var inventory = _inventory;
        return RunBusyOperationAsync("Deep Fat Friars", async token =>
        {
            using var session = await CharacterSession.OpenAsync(role, _accounts, token);
            var page = await session.ReadFriarsAsync(token);
            if (!ReferenceEquals(inventory, _inventory)) return;
            if (action is PerformFriarRitual && page != FriarsPageState.ReadyForRitual ||
                action is RequestFriarBlessing && page != FriarsPageState.RitualComplete)
            {
                ShowFriars(page, "The selected action is unavailable.");
                return;
            }
            if (action is null)
            {
                ShowFriars(page);
                return;
            }

            var result = await session.ExecuteAsync(action, token);
            page = await session.ReadFriarsAsync(token);
            if (!ReferenceEquals(inventory, _inventory)) return;
            inventory.ApplyLiveSnapshot(DesktopLiveDataProvider.ProjectInventory(role, result.Inventory, _catalog));
            var effects = string.Join(", ", result.Status.TimedEffects.Select(effect =>
                $"{effect.Name} ({effect.TurnsRemaining})"));
            ShowFriars(page, result.RejectionReason ?? effects);
        });
    }

    private void ShowFriars(FriarsPageState page, string? detail = null)
    {
        if (this.FindControl<TextBlock>("FriarsPageStatus") is { } status)
        {
            var description = page switch
            {
                FriarsPageState.CollectingItems => "Collect the three ritual items.",
                FriarsPageState.ReadyForRitual => "The ritual is available.",
                FriarsPageState.RitualComplete => "Ritual complete. Blessings are available.",
                _ => "Friars state unavailable."
            };
            status.Text = string.IsNullOrWhiteSpace(detail) ? description : $"{description} {detail}";
        }
        if (this.FindControl<Button>("FriarRitualButton") is { } ritual)
            ritual.IsEnabled = page == FriarsPageState.ReadyForRitual;
        foreach (var name in new[] { "FriarFoodButton", "FriarFamiliarButton", "FriarBoozeButton" })
            if (this.FindControl<Button>(name) is { } blessing)
                blessing.IsEnabled = page == FriarsPageState.RitualComplete;
    }

    private CombineCharacterItems? _combinePreview;
    private string? _combinePreviewName;

    public async void PreviewCharacterAcquisitionClick(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("CharacterAcquireItem")?.Text?.Trim();
        if (_catalog is null || string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Enter the item name.");
            return;
        }

        var inventory = _inventory;
        var role = _selectedRole;
        _combinePreview = null;
        _combinePreviewName = null;
        if (this.FindControl<Button>("CharacterCombineButton") is { } start) start.IsEnabled = false;
        await RunBusyOperationAsync("Finding item sources", async token =>
        {
            using var session = await CharacterSession.OpenAsync(role, _accounts, token);
            var account = await session.ReadAsync(token);
            var storage = await session.ReadStorageAsync(token);
            var mall = await _live.LoadCheapestMallPriceAsync(role, name, token);
            if (!ReferenceEquals(inventory, _inventory)) return;

            var lines = new List<string>();
            var target = _catalog.Items.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                var closetCount = storage.Closet.Items.Where(item => item.ItemId == target.Id).Sum(item => item.Quantity);
                var storageCount = storage.Storage.Items.Where(item => item.ItemId == target.Id).Sum(item => item.Quantity);
                lines.Add($"Closet {closetCount} · Storage {storageCount}");
            }
            if (mall is { } price) lines.Add($"Mall search from {price:N0} Meat");
            foreach (var coinmaster in ForeignBribeCatalog.Entries.Where(entry =>
                string.Equals(entry.ItemName, name, StringComparison.OrdinalIgnoreCase)))
                lines.Add($"Coinmaster · {coinmaster.CurrencyCost} {coinmaster.Currency}"
                    + (coinmaster.RequiresTicket ? " · ticket required" : ""));

            foreach (var recipe in CharacterRecipeCatalog.Find(_catalog, name))
            {
                lines.Add($"{recipe.Method} · {string.Join(" + ", recipe.IngredientNames)}");
                if (recipe.CombineAction is not { } combine) continue;
                var haveIngredients = !account.Inventory.IsStale &&
                    new[] { combine.FirstItemId, combine.SecondItemId }.GroupBy(id => id)
                        .All(group => account.Inventory.Items.Where(item =>
                            item.ItemId == group.Key.ToString(CultureInfo.InvariantCulture)).Sum(item => item.Quantity) >= group.Count());
                lines.Add(haveIngredients ? "Ingredients in inventory · may need meat paste" : "Ingredients missing from inventory");
                if (haveIngredients && _combinePreview is null)
                {
                    _combinePreview = combine;
                    _combinePreviewName = name;
                }
            }
            if (this.FindControl<Button>("CharacterCombineButton") is { } button) button.IsEnabled = _combinePreview is not null;
            if (this.FindControl<TextBlock>("CharacterAcquisitionStatus") is { } status)
                status.Text = lines.Count == 0 ? "No sources found." : string.Join("\n", lines);
        });
    }

    public async void CombineCharacterItemClick(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("CharacterAcquireItem")?.Text?.Trim();
        if (_combinePreview is not { } combine || !string.Equals(name, _combinePreviewName, StringComparison.Ordinal))
        {
            SetStatus("Find sources for the item before combining.");
            return;
        }
        await RunCharacterActionAsync(combine);
    }

    /// <summary>Moves one unit of the item to the closet, but only while that item is still the selected row.</summary>
    private Task MoveSelectedToClosetAsync(string itemId)
    {
        if (_inventory.SelectedItem?.ItemId != itemId || !int.TryParse(itemId, out var id) || id <= 0)
        {
            ReportInventoryResult("That item is no longer selected or shown; select it again.", error: true);
            return Task.CompletedTask;
        }
        return RunCharacterActionAsync(new MoveClosetItem(id, 1, IntoCloset: true));
    }

    public bool SupportsCloset => true;
    public bool SupportsInfo => true;

    public void MoveToCloset(ItemMenuTarget item)
        => _ = GuardedAsync($"Closet {item.Name}", () =>
        {
            _inventory.SelectedItem = _inventory.Items.FirstOrDefault(r => r.ItemId == item.ItemId);
            return MoveSelectedToClosetAsync(item.ItemId);
        });

    public void ShowInfo(ItemMenuTarget item)
    {
        var row = _inventory.Items.FirstOrDefault(r => r.ItemId == item.ItemId);
        if (row is null)
        {
            ReportInventoryResult($"{item.Name} is not in the loaded inventory; refresh and try again.", error: true);
            return;
        }
        _inventory.SelectedItem = row;
        ReportInventoryResult(InventoryActionPlanner.Describe(_inventory.Classify(row), row.Name, row.Quantity));
    }

    private void RenderInventoryActions(DesktopItemDetail detail)
    {
        if (_inventoryDetailPanel is null || !int.TryParse(detail.ItemId, out var id) || id <= 0)
            return;

        void AddButton(string label, Func<Task> execute, string style = "secondary")
        {
            var button = new Button
            {
                Content = label, Classes = { style }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, MinHeight = 38, FontSize = 14
            };
            if (style == "primary") button.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            button.Click += async (_, _) => await GuardedAsync(label, execute);
            _inventoryDetailPanel.Children.Add(button);
        }

        // Use/Eat/Drink/Chew and Equip come from the item classification and share one code path
        // with the context menu (overfill warning, gates, readable result).
        var row = _inventory.SelectedItem;
        if (row is null) return;
        var target = _inventory.ToMenuTarget(row);
        var quantityBox = _inventoryView?.FindControl<NumericUpDown>("InventoryQuantity");
        if (quantityBox is not null)
        {
            quantityBox.Maximum = Math.Max(1, row.Quantity);
            if (quantityBox.Value is null || quantityBox.Value > row.Quantity) quantityBox.Value = 1;
        }
        if (_inventoryView?.FindControl<StackPanel>("InventoryQuantityRow") is { } quantityRow)
            quantityRow.IsVisible = target.Use != ItemUseKind.None;
        int Quantity() => (int)Math.Clamp(quantityBox?.Value ?? 1, 1, Math.Max(1, row.Quantity));
        if (target.Use != ItemUseKind.None)
            AddButton(target.Use.ToString(), () => RunInventoryConsumeAsync(target, target.Use, Quantity()), "primary");
        var firstEquip = true;
        foreach (var slot in target.EquipSlots ?? Array.Empty<string>())
        {
            AddButton($"Equip · {slot}", () => RunInventoryEquipAsync(target, slot), firstEquip && target.Use == ItemUseKind.None ? "primary" : "secondary");
            firstEquip = false;
        }
        AddButton("Move one to closet", () => MoveSelectedToClosetAsync(detail.ItemId));
        var organ = _inventory.Classify(row);
        if (organ.Organ != ItemOrgan.None)
        {
            var cost = organ.OrganCost?.Size is { } size ? $"{size} per unit" : "cost unknown";
            _inventoryDetailPanel.Children.Add(new TextBlock
            {
                Text = $"{InventoryActionPlanner.Label(organ)}: {cost} · {_inventory.OrganSummary}",
                FontSize = 12, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
        }
    }

    public async void CompareCharacterEquipmentClick(object? sender, RoutedEventArgs e)
    {
        var modifier = this.FindControl<TextBox>("CharacterModifierGoal")?.Text?.Trim();
        if (_catalog is null || string.IsNullOrWhiteSpace(modifier)
            || this.FindControl<ComboBox>("CharacterSlotPicker")?.SelectedItem is not CharacterEquipmentSlot slot)
        {
            SetStatus("Select a slot and enter the modifier you want to compare.");
            return;
        }

        var inventory = _inventory;
        var role = _selectedRole;
        await RunBusyOperationAsync("Comparing equipment", async token =>
        {
            using var session = await CharacterSession.OpenAsync(role, _accounts, token);
            var snapshot = await session.ReadAsync(token);
            if (!ReferenceEquals(inventory, _inventory)) return;
            var suggestions = CharacterModifierCalculator.CompareSlot(_catalog, snapshot, slot, modifier);
            if (this.FindControl<TextBlock>("CharacterGearPreview") is { } preview)
                preview.Text = suggestions.Count == 0 ? "No verified catalog improvement for this slot."
                    : string.Join("\n", suggestions.Take(5).Select(row => $"{row.Name} · +{row.Improvement:0.##} {modifier}"))
                        + "\nKoL checks equip requirements when you equip an item.";
        });
    }

    public async void ReadCharacterEquipmentClick(object? sender, RoutedEventArgs e)
        => await RunCharacterActionAsync(null);

    public async void ReadCharacterStorageClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var inventory = _inventory;
        await RunBusyOperationAsync("Reading closet and storage", async token =>
        {
            using var session = await CharacterSession.OpenAsync(role, _accounts, token);
            var snapshot = await session.ReadStorageAsync(token);
            if (ReferenceEquals(inventory, _inventory)) ShowCharacterStorage(snapshot, role);
        });
    }

    public async void TakeCharacterStoredItemClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ComboBox>("CharacterStoredItem")?.SelectedItem is not CharacterStoredItemRow item)
        {
            SetStatus("Select an item in your closet or storage.");
            return;
        }

        await RunCharacterActionAsync(item.InCloset
            ? new MoveClosetItem(item.ItemId, 1, IntoCloset: false)
            : new PullStorageItem(item.ItemId, 1));
    }

    private void ShowCharacterStorage(CharacterStorageSnapshot snapshot, KoLibri.Core.Basis.AccountRole role)
    {
        if (this.FindControl<ComboBox>("CharacterStoredItem") is { } picker)
        {
            var closet = DesktopLiveDataProvider.ProjectInventory(role, snapshot.Closet, _catalog).Items;
            var storage = DesktopLiveDataProvider.ProjectInventory(role, snapshot.Storage, _catalog).Items;
            picker.ItemsSource = closet.Select(item => (Item: item, InCloset: true))
                .Concat(storage.Select(item => (Item: item, InCloset: false)))
                .Where(row => int.TryParse(row.Item.ItemId, out _))
                .Select(row => new CharacterStoredItemRow(int.Parse(row.Item.ItemId, CultureInfo.InvariantCulture),
                    row.InCloset, $"{(row.InCloset ? "Closet" : "Storage")} · {row.Item.Name} ({row.Item.Quantity})"))
                .ToArray();
        }
        if (this.FindControl<TextBlock>("CharacterStorageStatus") is { } status)
            status.Text = snapshot.Status.StoragePullsRemaining switch
            {
                < 0 => "Storage pulls unlimited",
                { } remaining => $"Storage pulls remaining: {remaining}",
                _ => "Storage pull limit unavailable"
            };
    }

    public async void RestCharacterClick(object? sender, RoutedEventArgs e)
        => await RunCharacterActionAsync(new RestAtCampground());

    public async void ApplyCharacterMoodClick(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("CharacterMoodEffect")?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !TryReadSkillId(out var id)
            || _catalog?.Skills.FirstOrDefault(skill => skill.Id == id) is not { } skill)
        {
            SetStatus("Enter an effect name and the skill (name or ID).");
            return;
        }

        var inventory = _inventory;
        var role = _selectedRole;
        await RunBusyOperationAsync("Checking mood", async token =>
        {
            using var session = await CharacterSession.OpenAsync(role, _accounts, token);
            if (!ReferenceEquals(inventory, _inventory)) return;
            var result = await session.EnsureEffectAsync(new CharacterMoodRule(name, id, skill.MpCost), token);
            if (!ReferenceEquals(inventory, _inventory)) return;
            inventory.ApplyLiveSnapshot(DesktopLiveDataProvider.ProjectInventory(role, result.Snapshot.Inventory, _catalog));
            SetStatus(result.Message);
        });
    }

    public async void CastCharacterSkillClick(object? sender, RoutedEventArgs e)
    {
        if (TryReadSkillId(out var id) && id > 0)
            await RunCharacterActionAsync(new CastCharacterSkill(id));
        else
            SetStatus("Enter a skill name or ID.");
    }

    public async void UnequipCharacterSlotClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ComboBox>("CharacterSlotPicker")?.SelectedItem is CharacterEquipmentSlot slot)
            await RunCharacterActionAsync(new UnequipInventoryItem(slot));
        else
            SetStatus("Select an equipment slot.");
    }

    public async void WearCharacterOutfitClick(object? sender, RoutedEventArgs e)
    {
        if (TryReadCharacterId("CharacterOutfitId", out var id) && id != 0)
            await RunCharacterActionAsync(new WearCharacterOutfit(id));
        else
            SetStatus("Enter an outfit ID.");
    }

    public async void SwitchCharacterFamiliarClick(object? sender, RoutedEventArgs e)
    {
        if (TryReadCharacterId("CharacterFamiliarId", out var id) && id > 0)
            await RunCharacterActionAsync(new SelectCharacterFamiliar(id));
        else
            SetStatus("Enter a familiar ID.");
    }

    public async void UnequipCharacterFamiliarClick(object? sender, RoutedEventArgs e)
        => await RunFamiliarEquipmentAsync(null);

    private Task RunFamiliarEquipmentAsync(int? itemId)
    {
        if (TryReadCharacterId("CharacterFamiliarId", out var id) && id > 0)
            return RunCharacterActionAsync(new EquipCharacterFamiliar(id, itemId));
        SetStatus("Enter a familiar ID.");
        return Task.CompletedTask;
    }

    private bool TryReadCharacterId(string name, out int id)
        => int.TryParse(this.FindControl<TextBox>(name)?.Text, CultureInfo.InvariantCulture, out id);

    private void ClearCharacterEquipment()
    {
        ShowFriars(FriarsPageState.Unknown);
        if (this.FindControl<TextBlock>("CharacterGearPreview") is { } gear) gear.Text = string.Empty;
        _combinePreview = null;
        _combinePreviewName = null;
        if (this.FindControl<Button>("CharacterCombineButton") is { } combine) combine.IsEnabled = false;
        if (this.FindControl<TextBlock>("CharacterAcquisitionStatus") is { } acquisition)
            acquisition.Text = string.Empty;
        if (this.FindControl<TextBlock>("CharacterEquipmentStatus") is { } status)
            status.Text = string.Empty;
        if (this.FindControl<TextBlock>("CharacterResourceStatus") is { } resources)
            resources.Text = string.Empty;
        if (this.FindControl<TextBlock>("CharacterStorageStatus") is { } storage)
            storage.Text = string.Empty;
        if (this.FindControl<ComboBox>("CharacterStoredItem") is { } storedItem)
            storedItem.ItemsSource = null;
        if (this.FindControl<TextBox>("CharacterFamiliarId") is { } familiar)
            familiar.Text = string.Empty;
    }

    private Task RunCharacterActionAsync(CharacterAction? action)
    {
        var role = _selectedRole;
        var inventory = _inventory;
        return RunBusyOperationAsync(action is null ? "Reading equipment" : "Updating character", async token =>
        {
            using var session = await CharacterSession.OpenAsync(role, _accounts, token);
            if (!ReferenceEquals(inventory, _inventory)) return;
            var snapshot = action is null
                ? await session.ReadAsync(token)
                : await session.ExecuteAsync(action, token);
            if (!ReferenceEquals(inventory, _inventory)) return;

            if (this.FindControl<TextBlock>("CharacterEquipmentStatus") is { } status)
                status.Text = string.Join(" · ", new[] { $"Familiar {FamiliarNameOrId(snapshot.Status.FamiliarId)}" }
                    .Concat(snapshot.Status.Equipment.Select(pair => $"{pair.Key} {ItemNameOrId(pair.Value)}")));
            if (this.FindControl<TextBlock>("CharacterResourceStatus") is { } resources)
            {
                var observed = snapshot.Status;
                var counters = $"Fullness {observed.Fullness?.ToString() ?? "?"} · Drunkenness {observed.Inebriety?.ToString() ?? "?"} · Spleen {observed.Spleen?.ToString() ?? "?"}";
                var pool = $"HP {observed.CurrentHp}/{observed.MaximumHp} · MP {observed.CurrentMp}/{observed.MaximumMp} · {observed.Adventures} adventures";
                var effects = string.Join(", ", observed.TimedEffects.Select(effect => $"{effect.Name} ({effect.TurnsRemaining})"));
                resources.Text = string.Join("\n", new[] { pool, counters, effects }.Where(line => line.Length > 0));
                if (_catalog is not null)
                {
                    var modifiers = CharacterModifierCalculator.Calculate(_catalog, observed);
                    if (modifiers.Values.Count > 0)
                        resources.Text += "\nCatalog modifiers: " + string.Join(", ",
                            modifiers.Values.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value:+0.##;-0.##;0}"));
                    if (modifiers.Unresolved.Count > 0)
                        resources.Text += $"\n{modifiers.Unresolved.Count} modifiers or sources need more data.";
                }
            }

            if (this.FindControl<TextBox>("CharacterFamiliarId") is { } familiar)
                familiar.Text = snapshot.Status.FamiliarId;
            var observedInventory = DesktopLiveDataProvider.ProjectInventory(role, snapshot.Inventory, _catalog);
            inventory.ApplyLiveSnapshot(observedInventory);
            if (action is MoveClosetItem or PullStorageItem)
            {
                var storage = await session.ReadStorageAsync(token);
                if (!ReferenceEquals(inventory, _inventory)) return;
                ShowCharacterStorage(storage, role);
            }
            if (ReferenceEquals(inventory, _inventory))
                SetStatus(snapshot.RejectionReason ?? (snapshot.Status.IsStale || snapshot.Inventory.IsStale
                    ? "KoL did not return a fresh character state."
                    : DescribeCharacterAction(action, snapshot.Status)));
        });
    }

    private static string DescribeCharacterAction(CharacterAction? action, KoLStatusSnapshot status)
    {
        if (action is null) return "Equipment loaded.";
        var confirmed = action switch
        {
            EquipInventoryItem equip => ObservedEquipment(status, equip.Slot)
                == equip.ItemId.ToString(CultureInfo.InvariantCulture),
            UnequipInventoryItem unequip => ObservedEquipment(status, unequip.Slot)
                is "0",
            SelectCharacterFamiliar familiar => status.FamiliarId == familiar.FamiliarId.ToString(CultureInfo.InvariantCulture),
            EquipCharacterFamiliar gear when status.FamiliarId == gear.FamiliarId.ToString(CultureInfo.InvariantCulture)
                => (status.Equipment.GetValueOrDefault("familiarequip") ?? status.Equipment.GetValueOrDefault("familiar"))
                    == (gear.ItemId ?? 0).ToString(CultureInfo.InvariantCulture),
            _ => (bool?)null
        };
        return confirmed switch
        {
            true => "Character change confirmed.",
            false => "KoL did not confirm the requested change. Character state refreshed.",
            null => "Character state refreshed."
        };
    }


    private static string? ObservedEquipment(KoLStatusSnapshot status, CharacterEquipmentSlot slot)
        => status.Equipment.GetValueOrDefault(CharacterActionClient.SlotName(slot)) ?? (slot switch
        {
            CharacterEquipmentSlot.Container => status.Equipment.GetValueOrDefault("back"),
            CharacterEquipmentSlot.Offhand => status.Equipment.GetValueOrDefault("off-hand"),
            _ => null
        });

}
