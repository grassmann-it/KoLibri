using System.Text.Json;
using Xunit;

namespace KoLibri.Tests;

/// <summary>reprice/compare against the fake KoL: read-only, own stores never count as competitors.</summary>
[Collection("Cli")]
public sealed class CliRepriceTests : IDisposable
{
    private const int MallbotPlayerId = 3856805;
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-reprice-").FullName;
    private readonly FakeKol _kol = new();
    private readonly MemoryAccountStore _store = MemoryAccountStore.WithMain();

    public CliRepriceTests()
    {
        _store.SaveAsync(new AccountRecord(new Account(new AccountId("mallbot"), "Mallbot", MallbotPlayerId, null, Array.Empty<string>()),
            "PHPSESSID=def", null, null), default).GetAwaiter().GetResult();
        KolibriCli.OptionsOverride = () => new KolibriOptions
        {
            HttpHandler = _kol,
            AccountStore = _store,
            StateDirectory = _state,
            DisableLimiterForTests = true
        };
        AgentGuard.TokenRootOverride = Path.Combine(_state, "token");
    }

    public void Dispose()
    {
        KolibriCli.OptionsOverride = null;
        AgentGuard.TokenRootOverride = null;
    }

    private async Task<(int Code, JsonElement Json, string Error)> Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(error);
        int code;
        try { code = await KolibriCli.RunAsync(args, _ => null); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        var text = output.ToString().Trim();
        return (code, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default, error.ToString());
    }

    private static string ShopPage(long price) => $$"""
        <table><tr class="deets"><td><input type="text" name="price[10881]" value="{{price}}"></td>
        <td><b>11-leaf clover</b></td><td>3</td>
        <td><span class="tohide">{{price:N0}}</span><input type="text" class="hideit price" value="{{price}}"></td>
        <td><input name="limit[10881]" value="0"></td></tr></table>
        """;

    private static string Row(int store, string name, long price, int stock) => $"""
        <tr class="graybelow" id="stock_{store}_10881">
          <td style="border:none"></td>
          <td class="small store"><a href="mallstore.php?whichstore={store}"><b>{name}</b></a></td>
          <td class="small stock">{stock}</td>
          <td class="small">&nbsp;</td>
          <td class="small price"><a href="mallstore.php?whichstore={store}">{price:N0}&nbsp;Meat</a></td>
        </tr>
        """;

    private static string Mall(params string[] rows) => $"""
        <center><b>Search Results:</b> <b>(Items 1-{rows.Length} of {rows.Length})</b></center>
        <div id='searchresults'><a name="searchresults"></a>
        <br /><a name="results_10881"></a>
        <table class="itemtable" summary="Stores selling 11-leaf clover">
        <tr class="blackabove" id="item_10881"><td><img src='x.gif' alt="11-leaf clover" title="11-leaf clover"></td>
        <td><b><a href="/mall.php?pudnuggler=%2211-leaf+clover%22">11-leaf clover</a></b></td></tr>
        {string.Join("\n", rows)}
        </table></div>
        """;

    private void Arrange(long ownPrice)
    {
        _kol.On("GET backoffice.php", ShopPage(ownPrice));
        // Mallbot's own store is the cheapest, a competitor sits at 2,000.
        _kol.On("POST mall.php", Mall(Row(MallbotPlayerId, "Mallbot", ownPrice, 3), Row(56226, "Crazy Gordon", 2000, 10)));
    }

    [Fact]
    public async Task Reprice_emits_apply_prices_lines_and_sends_no_write()
    {
        Arrange(ownPrice: 500);
        var (code, json, _) = await Cli("--experimental", "reprice", "--role", "mallbot", "--filter", "all");
        Assert.Equal(0, code);
        var row = json.GetProperty("results")[0];
        Assert.Equal("Up", row.GetProperty("direction").GetString());
        Assert.Equal(1990, row.GetProperty("suggestedPrice").GetInt64());
        Assert.Equal(1, row.GetProperty("ownListingsExcluded").GetInt32());
        Assert.Equal(2000, row.GetProperty("competitorFloor").GetInt64()); // own store at 500 is not the floor
        Assert.Equal("10881:500:1990", json.GetProperty("applyPrices")[0].GetString());
        Assert.Contains(MallbotPlayerId, json.GetProperty("ownStoreIds").EnumerateArray().Select(x => x.GetInt32()));
        Assert.DoesNotContain(_kol.Requests, r => r.Method != "GET" && !r.Path.StartsWith("/mall.php"));
        Assert.DoesNotContain(_kol.Requests, r => r.Path.Contains("managestore", StringComparison.OrdinalIgnoreCase) && r.Method == "POST");
    }

    [Fact]
    public async Task Compare_requires_item_and_keeps_0_4_field_names()
    {
        var (missingCode, _, error) = await Cli("--experimental", "compare", "--role", "mallbot");
        Assert.NotEqual(0, missingCode);
        Assert.Contains("--item", error);
        Assert.Empty(_kol.Requests);

        Arrange(ownPrice: 5000);
        var (code, json, _) = await Cli("--experimental", "compare", "--role", "mallbot", "--item", "10881");
        Assert.Equal(0, code);
        var row = json.GetProperty("results")[0];
        Assert.Equal(5000, row.GetProperty("currentPrice").GetInt64());
        Assert.Equal(2000, row.GetProperty("mallLowest").GetInt64());
        Assert.True(row.TryGetProperty("pricegunMedian", out _));
        Assert.Equal("11-leaf clover", row.GetProperty("displayName").GetString());
        Assert.Equal("Down", row.GetProperty("direction").GetString());
    }
}
