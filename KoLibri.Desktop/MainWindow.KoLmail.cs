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
    private async void ReadKoLmailInboxClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(KoLMailFolder.Inbox, 1);

    private async void ReadKoLmailSavedClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(KoLMailFolder.Saved, 1);

    private async void ReadKoLmailOutboxClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(KoLMailFolder.Outbox, 1);

    private async void PreviousKoLmailPageClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(_koLmailFolder, _koLmailPage - 1);

    private async void NextKoLmailPageClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(_koLmailFolder, _koLmailPage + 1);

    private async Task ReadKoLmailPageAsync(KoLMailFolder folder, int page)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading KoLmail", async token =>
        {
            var inbox = await _live.ReadMailboxPageAsync(role, folder, page, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            if (inbox is null)
            {
                if (this.FindControl<TextBlock>("KoLmailInboxStatus") is { } loginStatus)
                    loginStatus.Text = "Sign in to read KoLmail.";
                return;
            }
            ShowKoLmailPage(folder, inbox);
        });
    }

    private void ShowKoLmailPage(KoLMailFolder folder, KoLMailboxSnapshot inbox)
    {
        _koLmailFolder = folder;
        SetKoLmailDeleteConfirmation(false);
        SetKoLmailNavigation(inbox.Page, inbox.Recognized && inbox.HasNextPage);
        if (this.FindControl<ListBox>("KoLmailInboxRows") is { } rows)
        {
            rows.ItemsSource = inbox.Entries;
            rows.SelectedIndex = -1;
        }
        if (this.FindControl<TextBlock>("KoLmailInboxStatus") is { } status)
            status.Text = !inbox.Recognized ? "KoLmail response could not be read."
                : inbox.Entries.Count == 0 ? $"{folder} is empty."
                : $"{(folder == KoLMailFolder.Inbox ? "" : $"{folder} · ")}Page {inbox.Page} · {inbox.Entries.Count} messages.";
    }

    private async void SaveKoLmailClick(object? sender, RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("KoLmailInboxStatus")!;
        if (_koLmailFolder != KoLMailFolder.Inbox
            || this.FindControl<ListBox>("KoLmailInboxRows")?.SelectedItem is not KoLMailEntry message)
        {
            status.Text = "Select a message from the inbox to save it.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Saving KoLmail", async token =>
        {
            await _live.SaveInboxMessageAsync(role, message.Id, token);
            var saved = await _live.ReadMailboxPageAsync(role, KoLMailFolder.Saved, 1, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            if (saved is not null && saved.Recognized && saved.Entries.Any(entry => entry.Id == message.Id))
                ShowKoLmailPage(KoLMailFolder.Saved, saved);
            else
                status.Text = "Save was unconfirmed. Check Saved mail.";
        });
    }

    private void KoLmailSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => SetKoLmailDeleteConfirmation(false);

    private void SetKoLmailDeleteConfirmation(bool visible)
    {
        if (this.FindControl<Button>("KoLmailConfirmDelete") is { } confirm)
            confirm.IsVisible = visible;
    }

    private void DeleteKoLmailClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is null && this.FindControl<ListBox>("KoLmailInboxRows")?.SelectedItem is KoLMailEntry)
            SetKoLmailDeleteConfirmation(true);
    }

    private async void ConfirmDeleteKoLmailClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }
            || this.FindControl<ListBox>("KoLmailInboxRows")?.SelectedItem is not KoLMailEntry entry)
            return;

        SetKoLmailDeleteConfirmation(false);
        var role = _selectedRole;
        var current = _guts;
        var folder = _koLmailFolder;
        await RunBusyOperationAsync("Deleting KoLmail", async token =>
        {
            await _live.DeleteMailboxMessageAsync(role, folder, entry.Id, token);
            var snapshot = await _live.ReadMailboxPageAsync(role, folder, 1, token);
            if (snapshot is not null && role == _selectedRole && ReferenceEquals(current, _guts))
                ShowKoLmailPage(folder, snapshot);
        });
    }

    private async void SendKoLmailClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button send) return;

        var recipientBox = this.FindControl<TextBox>("KoLmailRecipient")!;
        var messageBox = this.FindControl<TextBox>("KoLmailMessage")!;
        var status = this.FindControl<TextBlock>("KoLmailSendStatus")!;
        var recipient = recipientBox.Text;
        var message = messageBox.Text ?? string.Empty;
        var attachmentBox = this.FindControl<TextBox>("KoLmailAttachments")!;
        var meatBox = this.FindControl<TextBox>("KoLmailMeat")!;
        var attachments = new List<KoLMailAttachment>();
        var itemIds = new HashSet<int>();
        foreach (var line in (attachmentBox.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length != 2 || !int.TryParse(pair[0], out var id) || id <= 0
                || !int.TryParse(pair[1], out var quantity) || quantity <= 0 || !itemIds.Add(id) || attachments.Count == 11)
            {
                status.Text = "Enter up to 11 distinct item IDs and positive quantities, one pair per line.";
                return;
            }
            attachments.Add(new KoLMailAttachment(id, quantity));
        }

        long meat = 0;
        if (!string.IsNullOrWhiteSpace(meatBox.Text) && (!long.TryParse(meatBox.Text, out meat) || meat < 0))
        {
            status.Text = "Enter a nonnegative whole Meat amount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(recipient) || (string.IsNullOrWhiteSpace(message) && attachments.Count == 0 && meat == 0))
        {
            status.Text = "Enter a recipient and a message or attachment.";
            return;
        }

        var role = _selectedRole;
        send.IsEnabled = false;
        recipientBox.IsEnabled = messageBox.IsEnabled = attachmentBox.IsEnabled = meatBox.IsEnabled = false;
        status.Text = $"Sending as {role}…";
        try
        {
            await RunBusyOperationAsync("Sending KoLmail", async token =>
            {
                if (!await _live.SendKoLmailAsync(role, recipient, message, attachments, meat, token)) return;
                status.Text = $"Message sent as {role}.";
                recipientBox.Text = string.Empty;
                messageBox.Text = attachmentBox.Text = meatBox.Text = string.Empty;
            });
            if (status.Text == $"Sending as {role}…")
                status.Text = "Delivery was unconfirmed. Check sent mail before retrying.";
        }
        finally
        {
            send.IsEnabled = true;
            recipientBox.IsEnabled = messageBox.IsEnabled = attachmentBox.IsEnabled = meatBox.IsEnabled = true;
        }
    }

    private void SetKoLmailNavigation(int page, bool hasNextPage)
    {
        _koLmailPage = page;
        if (this.FindControl<Button>("KoLmailPrevious") is { } previous) previous.IsEnabled = page > 1;
        if (this.FindControl<Button>("KoLmailNext") is { } next) next.IsEnabled = hasNextPage;
    }
}
