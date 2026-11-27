using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>Clan stash and chat over raw session pages (copied Core parsers), and the KoLmail row mapping.</summary>
public sealed class DesktopCommunityTests
{
    private const string StashPage = """
        <html><body><b>Goodies Hoard</b>
        <form name=takegoodies action=clan_stash.php method=post><input type=hidden name=action value=takegoodies>
        <select name=whichitem><option value=0>-select an item-</option>
        <option value=25 descid=123456>meat paste (12)</option>
        <option value=10881 descid=654321>11-leaf clover</option>
        <option value=41 descid=111>ice-cold Sir Schlitz (3) (-1)</option>
        </select></form></body></html>
        """;

    private static async Task<(FakeKol Kol, DesktopAccounts Accounts, DesktopLiveDataProvider Live)> Signed(FakeKol? kol = null)
    {
        kol ??= new FakeKol();
        var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
        await Task.CompletedTask;
        return (kol, accounts, new DesktopLiveDataProvider(accounts));
    }

    [Fact]
    public async Task Clan_stash_is_read_from_the_take_form()
    {
        var (kol, accounts, live) = await Signed(new FakeKol().On("GET clan_stash.php", StashPage));
        await using var _ = accounts;

        var stash = await live.ReadClanStashAsync(AccountRole.Main, default);

        Assert.NotNull(stash);
        Assert.True(stash!.CanRead);
        Assert.Equal(new[] { ("25", "meat paste", 12), ("10881", "11-leaf clover", 1), ("41", "ice-cold Sir Schlitz", 3) },
            stash.Items.Select(i => (i.ItemId, i.Name, i.Quantity)).ToArray());
    }

    [Fact]
    public async Task A_page_without_the_take_form_is_an_unreadable_stash()
    {
        var (_, accounts, live) = await Signed(new FakeKol().On("GET clan_stash.php", "<html>You are not in a clan.</html>"));
        await using var _ = accounts;
        var stash = await live.ReadClanStashAsync(AccountRole.Main, default);
        Assert.False(stash!.CanRead);
        Assert.Empty(stash.Items);
    }

    [Fact]
    public async Task Taking_from_the_stash_posts_the_0_4_form_and_reads_the_receipt()
    {
        var (kol, accounts, live) = await Signed(new FakeKol().On("POST clan_stash.php", "You acquire an item: <b>meat paste</b> (2)"));
        await using var _ = accounts;

        var taken = await live.TransferClanStashItemAsync(AccountRole.Main, "25", 2, deposit: false, default);

        Assert.True(taken);
        var form = kol.Requests.Single(r => r.Method == "POST" && r.Path.Contains("clan_stash.php")).Form;
        Assert.Contains("action=takegoodies", form);
        Assert.Contains("whichitem=25", form);
        Assert.Contains("quantity=2", form);
        Assert.Contains("ajax=1", form);
        Assert.Contains("pwd=", form);
    }

    [Fact]
    public async Task Depositing_uses_the_item1_and_qty1_fields()
    {
        var (kol, accounts, live) = await Signed(new FakeKol().On("POST clan_stash.php", "You add 3 items to the Goodies Hoard."));
        await using var _ = accounts;
        Assert.True(await live.TransferClanStashItemAsync(AccountRole.Main, "41", 3, deposit: true, default));
        var form = kol.Requests.Single(r => r.Method == "POST").Form;
        Assert.Contains("action=addgoodies", form);
        Assert.Contains("item1=41", form);
        Assert.Contains("qty1=3", form);
    }

    [Fact]
    public async Task Signed_out_reads_are_null_and_actions_ask_to_sign_in()
    {
        var kol = new FakeKol();
        await using var accounts = DesktopKit.Accounts(kol);
        var live = new DesktopLiveDataProvider(accounts);
        Assert.Null(await live.ReadClanStashAsync(AccountRole.Main, default));
        Assert.Null(await live.ReadChatAsync(AccountRole.Main, 0, default));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => live.SendChatAsync(AccountRole.Main, "/clan", "hi", 0, default));
        Assert.Contains("Sign in", error.Message);
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task Chat_send_encodes_the_graf_and_confirms_delivery_from_the_echo()
    {
        var echo = """{"msgs":[{"msg":"hello there","type":"public","mid":"901","who":{"name":"Tester","id":"1001"},"channel":"clan"}],"last":"901"}""";
        var (kol, accounts, live) = await Signed(new FakeKol().On("GET submitnewchat.php", echo));
        await using var _ = accounts;

        var batch = await live.SendChatAsync(AccountRole.Main, "/clan", "hello there", 900, default);

        Assert.True(batch.DeliveryConfirmed);
        Assert.Equal(901, batch.Cursor);
        var request = kol.Requests.Single(r => r.Path.Contains("submitnewchat.php"));
        Assert.Equal("GET", request.Method);
        Assert.Contains("playerid=1001", request.Path);
        Assert.Contains("j=1", request.Path);
        Assert.Equal("/clan hello there", Uri.UnescapeDataString(request.Path.Split("graf=")[1]));
    }

    [Fact]
    public async Task A_private_message_without_an_echo_is_unconfirmed()
    {
        var (kol, accounts, live) = await Signed(new FakeKol().On("GET submitnewchat.php", """{"msgs":[],"output":"<b>Nobody</b> is not online."}"""));
        await using var _ = accounts;

        var batch = await live.SendChatAsync(AccountRole.Main, "Some Player", "hi", 0, default);

        Assert.False(batch.DeliveryConfirmed);
        Assert.Equal("Nobody is not online.", batch.Output);
        Assert.Equal("/msg Some_Player hi", Uri.UnescapeDataString(kol.Requests.Single(r => r.Path.Contains("submitnewchat.php")).Path.Split("graf=")[1]));
    }

    [Theory]
    [InlineData("/clan", "/who")]
    [InlineData("bad name!", "hi")]
    public async Task Chat_refuses_commands_and_bad_targets_without_a_request(string target, string message)
    {
        var (kol, accounts, live) = await Signed();
        await using var _ = accounts;
        await Assert.ThrowsAsync<ArgumentException>(() => live.SendChatAsync(AccountRole.Main, target, message, 0, default));
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task Chat_read_skips_messages_up_to_the_cursor()
    {
        var feed = """{"msgs":[{"msg":"old","type":"public","mid":"5","channel":"clan","who":{"name":"A","id":"1"}},{"msg":"<i>new</i>","type":"private","mid":"6","who":{"name":"B","id":"2"}}],"last":"6"}""";
        var (kol, accounts, live) = await Signed(new FakeKol().On("GET newchatmessages.php", feed));
        await using var _ = accounts;

        var batch = await live.ReadChatAsync(AccountRole.Main, 5, default);

        var message = Assert.Single(batch!.Messages);
        Assert.Equal(("Private", "B", "new", "2"), (message.Channel, message.Sender, message.Text, message.SenderId));
        Assert.Contains("lasttime=5", kol.Requests.Single().Path);
    }

    [Fact]
    public void KoLmail_rows_map_the_library_entries()
    {
        var at = new DateTimeOffset(2026, 11, 20, 15, 30, 0, TimeSpan.Zero);
        var inbox = KoLMailEntry.From(new MailEntry("17", "Friend", at, "hi", [], 500), MailFolder.Inbox);
        Assert.Equal(("17", "Friend", "hi"), (inbox.Id, inbox.Correspondent, inbox.Text));
        Assert.True(inbox.HasAttachedMeat);
        Assert.Equal("500 Meat", inbox.AttachedMeatLabel);
        Assert.NotEqual(string.Empty, inbox.Date);

        var sent = KoLMailEntry.From(new MailEntry("18", "Friend", null, "yo", [], 0), MailFolder.Outbox);
        Assert.Equal("To Friend", sent.Correspondent);
        Assert.Equal(string.Empty, sent.Date);
        Assert.False(sent.HasAttachedMeat);
    }
}
