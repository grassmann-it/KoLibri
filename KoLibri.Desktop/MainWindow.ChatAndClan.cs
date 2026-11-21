using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using KolArbitrage.Core.Spiel;


namespace KoLibri.Desktop;

public partial class MainWindow
{
    private async void ReadKoLChatClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading KoL chat", async token =>
        {
            var batch = await _live.ReadChatAsync(role, _chatCursor, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            var status = this.FindControl<TextBlock>("KoLChatStatus")!;
            if (batch is null)
            {
                status.Text = "Sign in to read KoL chat.";
                return;
            }

            AppendKoLChat(batch);
            status.Text = _chatMessages.Count == 0 ? "No chat messages." : "";
        });
    }

    private async void ReadKoLChatContactsClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading KoL contacts", async token =>
        {
            var contacts = await _live.ReadChatContactsAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<ComboBox>("KoLChatContacts")!.ItemsSource = contacts;
            this.FindControl<TextBlock>("KoLChatStatus")!.Text = contacts switch
            {
                null => "Sign in to read KoL contacts.",
                { Count: 0 } => "No KoL contacts.",
                _ => ""
            };
        });
    }

    private void KoLChatContactChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: KoLChatContact contact }
            && this.FindControl<TextBox>("KoLChatRecipient") is { } recipient)
            recipient.Text = contact.PlayerId;
    }

    private async void SendKoLChatClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null) return;
        var recipient = this.FindControl<TextBox>("KoLChatRecipient")!;
        var draft = this.FindControl<TextBox>("KoLChatDraft")!;
        var status = this.FindControl<TextBlock>("KoLChatStatus")!;
        var channel = this.FindControl<ComboBox>("KoLChatChannel")?.SelectedItem as string;
        var target = string.IsNullOrWhiteSpace(recipient.Text) && channel?.StartsWith('/') == true
            ? channel : recipient.Text;
        var message = draft.Text;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(message))
        {
            status.Text = "Choose a channel or a private recipient and enter a message.";
            return;
        }
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Sending KoL chat", async token =>
        {
            var batch = await _live.SendChatAsync(role, target, message, _chatCursor, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            AppendKoLChat(batch);
            status.Text = batch.DeliveryConfirmed ? "Chat message sent."
                : batch.Output ?? "Delivery unconfirmed. Check chat before retrying.";
            if (batch.DeliveryConfirmed && draft.Text == message) draft.Text = string.Empty;
        });
    }

    private void AppendKoLChat(KoLChatBatch batch)
    {
        _chatCursor = batch.Cursor;
        var ids = _chatMessages.Select(message => message.Id).OfType<string>().ToHashSet();
        foreach (var message in batch.Messages)
            if (message.Id is null or "" or "0" || ids.Add(message.Id))
                _chatMessages.Add(message);
        if (_chatMessages.Count > 300)
            _chatMessages.RemoveRange(0, _chatMessages.Count - 300);
        RefreshKoLChatChannels();
    }

    private void RefreshKoLChatChannels()
    {
        if (this.FindControl<ComboBox>("KoLChatChannel") is not { } channel) return;
        var previous = channel.SelectedItem as string;
        var channels = new[] { "All channels" }.Concat(_chatMessages.Select(message => message.Channel)
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase)).ToArray();
        channel.ItemsSource = channels;
        channel.SelectedIndex = Math.Max(0, Array.IndexOf(channels, previous));
        RefreshKoLChatRows();
    }

    private void KoLChatChannelChanged(object? sender, SelectionChangedEventArgs e) => RefreshKoLChatRows();

    private void RefreshKoLChatRows()
    {
        if (this.FindControl<ItemsControl>("KoLChatRows") is not { } rows) return;
        var channel = this.FindControl<ComboBox>("KoLChatChannel")?.SelectedItem as string;
        rows.ItemsSource = _chatMessages.Where(message => channel is null or "All channels" || message.Channel == channel)
            .ToArray();
    }

    private async void ReadClanProfileClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading clan membership", async token =>
        {
            var profile = await _live.ReadClanProfileAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            var rows = this.FindControl<ItemsControl>("ClanMembers")!;
            rows.ItemsSource = null;
            if (this.FindControl<TextBlock>("ClanProfileStatus") is { } status)
                status.Text = profile switch
                {
                    null => "Sign in to see clan membership.",
                    { IsMember: false } => "No clan membership.",
                    { Title: { Length: > 0 } title } => $"{profile.Name} · {title}",
                    _ => profile.Name
                };
            if (profile?.ClanId is not { } clanId) return;
            var members = await _live.ReadClanMembersAsync(role, clanId, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                rows.ItemsSource = members;
        });
    }

    private async void ReadClanLoungeClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading clan VIP lounge", async token =>
        {
            var lounge = await _live.ReadClanLoungeAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<ItemsControl>("ClanLoungeFacilities")!.ItemsSource = lounge?.Facilities;
            this.FindControl<Button>("UseClanLookingGlass")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("lookingglass", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<Button>("UseClanCrimboTree")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("crimbotree", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<Button>("PlayClanKlaw")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("klaw", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanHotTubControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("hottub", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanPoolControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("pooltable", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanShowerControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("shower", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanSwimControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("swimmingpool", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanFaxControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("faxmachine", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = lounge switch
            {
                null => "Sign in to see the clan VIP lounge.",
                { HasAccess: false } => "Clan VIP lounge unavailable.",
                { Facilities.Count: 0 } => "No lounge facilities listed.",
                _ => $"{lounge.Facilities.Count} lounge facilities."
            };
        });
    }

    private async void UseClanLookingGlassClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Using clan looking glass", async token =>
        {
            var acquired = await _live.UseClanLookingGlassAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = acquired
                    ? "Received a DRINK ME potion."
                    : "No DRINK ME potion received. The daily use may already be spent.";
        });
    }

    private async void UseClanCrimboTreeClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Checking clan Crimbo Tree", async token =>
        {
            var result = await _live.UseClanCrimboTreeAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result switch
            {
                { ReceivedGift: true } => "Received a Crimbo gift.",
                { DaysUntilGift: 1 } => "Check the Crimbo Tree again tomorrow.",
                { DaysUntilGift: { } days } => $"Check the Crimbo Tree again in {days} days.",
                _ => "KoL did not confirm the Crimbo Tree status."
            };
        });
    }

    private async void PlayClanKlawClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Playing Deluxe Mr. Klaw", async token =>
        {
            var result = await _live.PlayClanKlawAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result switch
            {
                KoLClanKlawResult.Prize => "Won a Deluxe Mr. Klaw prize.",
                KoLClanKlawResult.Played => "Played Deluxe Mr. Klaw. No prize received.",
                KoLClanKlawResult.DailyLimit => "The Deluxe Mr. Klaw daily limit is reached.",
                _ => "KoL did not confirm the Klaw result."
            };
        });
    }

    private async void UseClanHotTubClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanHotTubControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        var consent = this.FindControl<CheckBox>("AllowHotTubCurseRemoval")!;
        var allowRemovingCurse = consent.IsChecked == true;
        consent.IsChecked = false;
        await RunBusyOperationAsync("Using clan Hot Tub", async token =>
        {
            var result = await _live.UseClanHotTubAsync(role, allowRemovingCurse, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result;
        });
    }

    private async void PlayClanPoolClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanPoolControls")?.IsVisible != true) return;
        var stance = (KoLClanPoolStance)(this.FindControl<ComboBox>("ClanPoolStance")!.SelectedIndex + 1);
        var status = this.FindControl<TextBlock>("ClanLoungeStatus")!;
        if (!Enum.IsDefined(stance))
        {
            status.Text = "Choose a pool stance.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Playing clan pool", async token =>
        {
            var result = await _live.PlayClanPoolAsync(role, stance, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            status.Text = result switch
            {
                KoLClanPoolResult.Won => "Won the pool game.",
                KoLClanPoolResult.Lost => "Lost the pool game.",
                KoLClanPoolResult.PlayedAgainstSelf => "Played a game of pool against yourself.",
                KoLClanPoolResult.DailyLimit => "The pool daily limit is reached.",
                _ => "KoL did not confirm the pool game."
            };
        });
    }

    private async void TakeClanShowerClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanShowerControls")?.IsVisible != true) return;
        var temperature = (KoLClanShowerTemperature)(this.FindControl<ComboBox>("ClanShowerTemperature")!.SelectedIndex + 1);
        var status = this.FindControl<TextBlock>("ClanLoungeStatus")!;
        if (!Enum.IsDefined(temperature))
        {
            status.Text = "Choose a shower temperature.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Taking April Shower", async token =>
        {
            var result = await _live.TakeClanShowerAsync(role, temperature, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            status.Text = result switch
            {
                KoLClanShowerResult.ShowerTaken => "April Shower taken.",
                KoLClanShowerResult.DailyLimit => "Today's April Shower is already used.",
                _ => "KoL did not confirm the shower."
            };
        });
    }

    private async void SwimClanLapsClick(object? sender, RoutedEventArgs e)
        => await SwimInClanPoolAsync(KoLClanSwimWorkout.Laps);

    private async void SwimClanSprintsClick(object? sender, RoutedEventArgs e)
        => await SwimInClanPoolAsync(KoLClanSwimWorkout.Sprints);

    private async Task SwimInClanPoolAsync(KoLClanSwimWorkout workout)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanSwimControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Using clan swimming pool", async token =>
        {
            var result = await _live.SwimInClanPoolAsync(role, workout, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result switch
            {
                { DailyLimit: true } => "Today's swimming workout is already used.",
                { Repetitions: { } count } when workout == KoLClanSwimWorkout.Laps => $"Swam {count} laps.",
                { Repetitions: { } count } => $"Completed {count} submarine sprints.",
                _ => "KoL did not confirm the swimming workout."
            };
        });
    }

    private async void ClanCannonballClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<StackPanel>("ClanSwimControls")?.IsVisible == true
            && this.FindControl<WrapPanel>("ClanPoolChoiceControls")?.IsVisible == false)
            await RunClanPoolChoiceAsync(null);
    }

    private async void ClanPoolHandstandClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Handstand);

    private async void ClanPoolDiveClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Dive);

    private async void ClanPoolBlinkClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Blink);

    private async void LeaveClanPoolClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Leave);

    private async void CheckClanPoolChoiceClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Checking clan pool", async token =>
        {
            var choice = await _live.ReadClanPoolChoiceAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts) && choice is not null)
                ShowClanPoolChoice(choice);
        });
    }

    private async Task RunClanPoolChoiceAsync(KoLClanPoolMove? move)
    {
        if (_refreshCancellation is not null
            || (move is not null && this.FindControl<WrapPanel>("ClanPoolChoiceControls")?.IsVisible != true))
            return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Using clan pool choice", async token =>
        {
            var choice = await _live.SubmitClanPoolChoiceAsync(role, move, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts)) ShowClanPoolChoice(choice);
        });
    }

    private void ShowClanPoolChoice(KoLClanPoolChoice choice)
    {
        this.FindControl<WrapPanel>("ClanPoolChoiceControls")!.IsVisible = choice.IsActive;
        this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = choice.Treasure is { } item
            ? $"Found {item} in the pool."
            : choice.IsActive ? "In the clan pool." : "No active clan pool choice.";
    }

    private async void ReceiveClanFaxClick(object? sender, RoutedEventArgs e)
        => await TransferClanFaxAsync(send: false);

    private async void SendClanFaxClick(object? sender, RoutedEventArgs e)
        => await TransferClanFaxAsync(send: true);

    private async void InspectClanFaxClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanFaxControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Inspecting clan photocopy", async token =>
        {
            var monster = await _live.ReadClanPhotocopyMonsterAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = monster is null
                    ? "No copied monster identified." : $"Photocopied monster: {monster}.";
        });
    }

    private async Task TransferClanFaxAsync(bool send)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanFaxControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync(send ? "Sending clan fax" : "Receiving clan fax", async token =>
        {
            var completed = await _live.TransferClanFaxAsync(role, send, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            var status = this.FindControl<TextBlock>("ClanLoungeStatus")!;
            status.Text = completed
                ? send ? "Photocopy sent." : "Photocopy received."
                : "KoL did not confirm the fax transfer.";
            if (!completed || send) return;
            var monster = await _live.ReadClanPhotocopyMonsterAsync(role, token);
            if (monster is not null && role == _selectedRole && ReferenceEquals(current, _guts))
                status.Text = $"Photocopy received: {monster}.";
        });
    }

    private async void ReadClanStashClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading clan stash", async token =>
        {
            var stash = await _live.ReadClanStashAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                ShowClanStash(stash);
        });
    }

    private async void TakeClanStashClick(object? sender, RoutedEventArgs e)
    {
        var rows = this.FindControl<ListBox>("ClanStashItems")!;
        var status = this.FindControl<TextBlock>("ClanStashStatus")!;
        var quantityBox = this.FindControl<TextBox>("ClanStashQuantity")!;
        if (_refreshCancellation is not null) return;
        if (rows.SelectedItem is not KoLClanStashItem item)
        {
            status.Text = "Select a stash item to take.";
            return;
        }
        if (!int.TryParse(quantityBox.Text, out var quantity) || quantity <= 0 || quantity > item.Quantity)
        {
            status.Text = $"Enter a quantity between 1 and {item.Quantity}.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        rows.ItemsSource = null;
        await RunBusyOperationAsync("Taking clan stash item", async token =>
        {
            var acquired = await _live.TransferClanStashItemAsync(role, item.ItemId, quantity, false, token);
            var updated = await _live.ReadClanStashAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            ShowClanStash(updated);
            status.Text = acquired ? $"KoL reported acquiring {item.Name}." : "KoL did not confirm the withdrawal.";
        });
    }

    private async void LoadClanStashDepositItemsClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading inventory for clan stash", async token =>
        {
            var inventory = await _live.LoadInventoryAsync(role, true, _catalog, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            ShowClanDepositInventory(inventory);
            if (inventory is { HasSession: false } or { IsStale: true })
                this.FindControl<TextBlock>("ClanStashStatus")!.Text = "Live inventory is unavailable.";
        });
    }

    private async void DepositClanStashClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null) return;
        var picker = this.FindControl<ComboBox>("ClanStashDepositItems")!;
        var status = this.FindControl<TextBlock>("ClanStashStatus")!;
        if (picker.SelectedItem is not DesktopInventoryItem item)
        {
            status.Text = "Select an inventory item to deposit.";
            return;
        }
        var quantityBox = this.FindControl<TextBox>("ClanStashDepositQuantity")!;
        if (!int.TryParse(quantityBox.Text, out var quantity) || quantity <= 0 || quantity > item.Quantity)
        {
            status.Text = $"Enter a quantity between 1 and {item.Quantity}.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        picker.ItemsSource = null;
        await RunBusyOperationAsync("Depositing clan stash item", async token =>
        {
            var deposited = await _live.TransferClanStashItemAsync(role, item.ItemId, quantity, true, token);
            var stash = await _live.ReadClanStashAsync(role, token);
            var inventory = await _live.LoadInventoryAsync(role, true, _catalog, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            ShowClanStash(stash);
            ShowClanDepositInventory(inventory);
            status.Text = deposited ? $"KoL reported depositing {item.Name}." : "KoL did not confirm the deposit.";
        });
    }

    private void ShowClanDepositInventory(DesktopInventorySnapshot inventory)
    {
        var picker = this.FindControl<ComboBox>("ClanStashDepositItems")!;
        picker.ItemsSource = inventory.HasSession && !inventory.IsStale
            ? inventory.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : null;
        picker.SelectedIndex = -1;
    }

    private void ShowClanStash(KoLClanStashSnapshot? stash)
    {
        this.FindControl<ListBox>("ClanStashItems")!.ItemsSource = stash?.Items;
        this.FindControl<TextBlock>("ClanStashStatus")!.Text = stash switch
        {
            null => "Sign in to see the clan stash.",
            { CanRead: false } => "Clan stash list unavailable.",
            { Items.Count: 0 } => "The clan stash is empty.",
            _ => $"{stash.Items.Count} item types in the clan stash."
        };
    }

    private int _koLmailPage = 1;
    private KoLMailFolder _koLmailFolder;
}
