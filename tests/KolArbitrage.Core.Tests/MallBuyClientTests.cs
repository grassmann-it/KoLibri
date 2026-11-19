using KolArbitrage.Core.Daily;
using Xunit;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Tests for the pieces that talk to KoL's HTML: the store password hash
/// scraper and the burning-leaves guard. Both sit in front of a write, so
/// a wrong answer here costs meat or leaves.
/// </summary>
public sealed class MallBuyClientTests
{
    [Fact]
    public void Password_hash_is_read_from_the_input_tag()
    {
        var html = "<form action=mallstore.php><input type=hidden name=\"pwd\" value=\"abc123\"></form>";
        Assert.Equal("abc123", MallBuyClient.ExtractPasswordHash(html));
    }

    [Fact]
    public void Password_hash_is_read_from_single_quotes()
    {
        var html = "<input type='hidden' name='pwd' value='xyz789'>";
        Assert.Equal("xyz789", MallBuyClient.ExtractPasswordHash(html));
    }

    [Fact]
    public void Password_hash_is_read_from_a_query_string()
    {
        var html = "<a href=\"mallstore.php?whichstore=1&pwd=hash42\">buy</a>";
        Assert.Equal("hash42", MallBuyClient.ExtractPasswordHash(html));
    }

    [Fact]
    public void Password_hash_is_read_from_the_javascript_form()
    {
        var html = "<script>var pwd = \"hashfromjs\";</script>";
        Assert.Equal("hashfromjs", MallBuyClient.ExtractPasswordHash(html));
    }

    [Fact]
    public void Missing_hash_yields_null()
    {
        Assert.Null(MallBuyClient.ExtractPasswordHash("<html><body>nothing</body></html>"));
        Assert.Null(MallBuyClient.ExtractPasswordHash(""));
    }

    [Fact]
    public void Store_id_is_parsed_from_the_listing()
    {
        Assert.True(MallBuyClient.TryParseStoreId("294980", out var storeId));
        Assert.Equal(294980, storeId);
        Assert.False(MallBuyClient.TryParseStoreId("not-a-number", out _));
        Assert.False(MallBuyClient.TryParseStoreId(null, out _));
    }
}

/// <summary>
/// The burning-leaves choice burns exactly the number of leaves we ask for
/// and happily takes the whole pile. The dispatcher therefore only allows
/// one leaf or the 222-leaf shortener.
/// </summary>
public sealed class BurningLeavesGuardTests
{
    [Fact]
    public async Task Burning_more_than_one_leaf_is_refused()
    {
        var dispatcher = new FreePullDispatcher(new HttpClient(), FreePullDispatcher.DefaultBaseUri);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            dispatcher.BurnLeavesAsync(
                new KolArbitrage.Core.Accounts.AccountCredentials { CookieHeader = "x" }, 5));
    }

    [Fact]
    public async Task Burning_zero_leaves_is_refused()
    {
        var dispatcher = new FreePullDispatcher(new HttpClient(), FreePullDispatcher.DefaultBaseUri);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            dispatcher.BurnLeavesAsync(
                new KolArbitrage.Core.Accounts.AccountCredentials { CookieHeader = "x" }, 0));
    }

    [Fact]
    public void The_day_shortener_costs_222_leaves()
    {
        Assert.Equal(222, FreePullDispatcher.DayShortenerLeafCost);
    }

    [Fact]
    public void The_burning_leaves_page_is_parsed()
    {
        var html = "<p>You have <b>37,191</b> leaves to throw in the fire. "
            + "You've stoked the fire with <b>2</b> random leaves today.</p>"
            + "<form action=choice.php><input type=hidden name=\"pwd\" value=\"tok\"></form>";
        var page = BurningLeavesPage.Parse(html);
        Assert.Equal(37191, page.Leaves);
        Assert.Equal(2, page.BurnedToday);
        Assert.Equal("choice.php", page.FormAction);
        Assert.Equal("tok", page.Fields["pwd"]);
    }

    [Fact]
    public void The_burning_leaves_page_keeps_the_callers_choice_fields()
    {
        var html = "<form action=choice.php>"
            + "<input type=hidden name=\"whichchoice\" value=\"9999\">"
            + "<input type=hidden name=\"leaves\" value=\"9999\">"
            + "<input type=hidden name=\"pwd\" value=\"tok\"></form>";
        var page = BurningLeavesPage.Parse(html);
        Assert.False(page.Fields.ContainsKey("whichchoice"));
        Assert.False(page.Fields.ContainsKey("leaves"));
        Assert.True(page.Fields.ContainsKey("pwd"));
    }
}
