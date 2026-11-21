using KolArbitrage.Core.Integration;
using KolArbitrage.Core.Security;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class KoLMailboxTests
{
    [Fact]
    public void Inbox_parser_reads_sender_date_and_text_and_keeps_message_boundaries()
    {
        var inbox = KoLMailbox.ParseInbox("""
            <table>
            <tr><td valign=top><input type=checkbox name="101"></td><td>
            <b>From:</b> <a href="showplayer.php?who=5">Player &amp; Co</a> [<a href="reply.php">reply</a>]<br>
            <b>Date:</b> Friday 10:00<br>First line<br>Second line
            </td></tr>
            <tr><td valign="top"><input name="102" type="checkbox"></td><td>
            <b>From:</b> System<br><b>Date:</b> Saturday 10:00<br>System notice
            </td></tr><tr><td><b>X</b></td></tr></table>
            """);

        Assert.True(inbox.Recognized);
        Assert.Collection(inbox.Entries,
            first =>
            {
                Assert.Equal("101", first.Id);
                Assert.Equal("Player & Co", first.Sender);
                Assert.Equal("Friday 10:00", first.Date);
                Assert.Equal("First line\nSecond line", first.Text);
            },
            second => Assert.Equal(new KoLMailEntry("102", "System", "Saturday 10:00", "System notice"), second));
    }

    [Fact]
    public void Inbox_separates_the_meat_attachment_and_leaves_plain_text_in_the_message()
    {
        var inbox = KoLMailbox.ParseInbox("""
            <td valign=top><input name="101"><b>From:</b> Player<br>
            <b>Date:</b> Friday<br>You gain 9 Meat (quoted).<br>
            <img src="/images/itemimages/meat.gif" height=30 width=30 alt="Meat">You gain 1,234 Meat
            </td><b>X</b>
            """);

        var message = Assert.Single(inbox.Entries);
        Assert.Equal(1234L, message.AttachedMeat);
        Assert.True(message.HasAttachedMeat);
        Assert.Equal("You gain 9 Meat (quoted).", message.Text);
    }

    [Theory]
    [InlineData("Messages: Inbox, page 1 (1 - 10 of 12)", 1, true)]
    [InlineData("Messages: Inbox, page 2 (11 - 12 of 12)", 2, false)]
    [InlineData("Messages: Inbox, page 1 (1 message)", 1, false)]
    public void Inbox_parser_reads_pagination(string heading, int page, bool hasNext)
    {
        var inbox = KoLMailbox.ParseInbox($"""
            <b>{heading}</b>
            <td valign=top><input name="101"><b>From:</b> Player<br>
            <b>Date:</b> Friday<br>Hello</td>
            """);

        Assert.True(inbox.Recognized);
        Assert.Equal(page, inbox.Page);
        Assert.Equal(hasNext, inbox.HasNextPage);
    }

    [Fact]
    public void Empty_mailbox_and_unrecognized_page_are_distinct()
    {
        var empty = KoLMailbox.ParseInbox("<p>There are no messages in this mailbox.</p>");
        Assert.True(empty.Recognized);
        Assert.Empty(empty.Entries);
        Assert.False(KoLMailbox.ParseInbox("<p>Unexpected page</p>").Recognized);
    }

    [Fact]
    public async Task Inbox_reader_fetches_the_selected_page_by_get()
    {
        using var http = new HttpClient(new InboxPageHandler());
        var inbox = await new KoLAccountReadClient(http).FetchInboxPageAsync(2);

        Assert.True(inbox.Recognized);
        Assert.Equal(2, inbox.Page);
        Assert.False(inbox.HasNextPage);
        Assert.Equal("Second page", Assert.Single(inbox.Entries).Text);
    }

    [Fact]
    public async Task Inbox_reader_reports_expiry_when_redirected_to_login()
    {
        using var http = new HttpClient(new InboxPageHandler(redirectToLogin: true));
        var error = await Assert.ThrowsAsync<KoLHtmlResponseException>(
            () => new KoLAccountReadClient(http).FetchInboxPageAsync(2));

        Assert.Equal(KoLHtmlResponseKind.LoginPage, error.Kind);
    }

    private sealed class InboxPageHandler(bool redirectToLogin = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/messages.php?box=Inbox&begin=2", request.RequestUri!.PathAndQuery);
            if (redirectToLogin)
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("/login.php", UriKind.Relative);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <b>Messages: Inbox, page 2 (11 - 11 of 11)</b>
                    <td valign=top><input name="111"><b>From:</b> Player<br>
                    <b>Date:</b> Friday<br>Second page</td>
                    """)
            });
        }
    }
}
