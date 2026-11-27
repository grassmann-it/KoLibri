using System.Net;
using System.Text.Json;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

[Collection(ConsoleCollection.Name)] // runs the CLI, which writes to Console.Out
public sealed class PirateKeyClientTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Html", "PirateKeys");
    private static string AssemblePage => File.ReadAllText(Path.Combine(FixtureDir, "assemble-response.html"));
    private static string KeyFormPage => File.ReadAllText(Path.Combine(FixtureDir, "key-form-main-php.html"));
    private const string MessageUrl = "inventory.php?which=&action=message";

    private static (PirateKeyClient Client, ScriptedHandler Handler, HttpClient Http) Create(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    {
        var handler = new ScriptedHandler(script);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.test/") };
        var security = new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } };
        var client = new PirateKeyClient(http, "fixture-pwd", security,
            new AccountReadPolicy { MinimumRequestInterval = TimeSpan.Zero, Security = security });
        return (client, handler, http);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Inv(string json) => r => Ok(r, json);
    private static Func<HttpRequestMessage, HttpResponseMessage> Html(string html) => r => Ok(r, html);
    private static Func<HttpRequestMessage, HttpResponseMessage> Redirect(string location) => r =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = r, Content = new StringContent("") };
        response.Headers.Location = new Uri(location, UriKind.Relative);
        return response;
    };
    private static HttpResponseMessage Ok(HttpRequestMessage r, string body)
        => new(HttpStatusCode.OK) { RequestMessage = r, Content = new StringContent(body) };

    [Fact]
    public void Fixture_form_offers_exactly_the_fixed_28_items()
    {
        var form = ItemChoiceForm.Parse(KeyFormPage, PirateKeyClient.CompleteKey, "rare");
        Assert.NotNull(form);
        Assert.Equal("1", form!.HiddenFields["qty"]);
        Assert.Equal("PWD", form.HiddenFields["pwd"]);
        Assert.Equal(28, form.Options.Count);
        Assert.Equal(PirateKeyClient.RareOptions, form.Options);
        Assert.Null(ItemChoiceForm.Parse(KeyFormPage, 999, "rare"));
        Assert.Null(ItemChoiceForm.Parse(AssemblePage, PirateKeyClient.CompleteKey, "rare"));
    }

    [Theory]
    [InlineData("11449", 11449)]
    [InlineData("elf guard field manual: extortion", 11449)]
    [InlineData("whalegun", 11431)]
    [InlineData("Encyclopedia", 11450)]
    public void Resolves_rare_items_by_id_or_name(string text, int expected)
        => Assert.Equal(expected, PirateKeyClient.ResolveRare(text, out _)!.Value);

    [Theory]
    [InlineData("12345")]
    [InlineData("Field Manual")]
    [InlineData("seal tooth")]
    public void Unknown_or_ambiguous_rare_items_are_refused(string text)
    {
        Assert.Null(PirateKeyClient.ResolveRare(text, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public async Task Assemble_uses_alpha_once_per_key_and_confirms_each_by_inventory_delta()
    {
        var (client, handler, http) = Create(
            Inv("""{"11386":2,"11387":2,"11388":2,"11389":2}"""),
            Html(AssemblePage),
            Inv("""{"11386":1,"11387":1,"11388":1,"11389":1,"11473":1}"""),
            Html(AssemblePage),
            Inv("""{"11473":2}"""));
        using var _ = http;

        var result = await client.AssembleAsync(2);

        Assert.True(result.Complete);
        Assert.Equal(2, result.Confirmed);
        Assert.Equal(2, result.Before.AssemblableSets);
        Assert.Equal(2, result.After!.Complete);
        var uses = handler.Requests.Where(r => r.AbsolutePath == "/inv_use.php").ToList();
        Assert.Equal(2, uses.Count);
        Assert.All(uses, u => Assert.Equal("?whichitem=11386&ajax=1&pwd=fixture-pwd", u.Query));
        Assert.All(handler.Methods, m => Assert.Equal(HttpMethod.Get, m));
        Assert.Contains("You combine the four parts", result.Steps[0].Message);
        Assert.Equal(-1, result.Steps[0].InventoryDelta[PirateKeyClient.Alpha]);
        Assert.Equal(1, result.Steps[0].InventoryDelta[PirateKeyClient.CompleteKey]);
    }

    [Fact]
    public async Task Assemble_refuses_more_keys_than_sets_without_sending_a_use()
    {
        var (client, handler, http) = Create(Inv("""{"11386":3,"11387":3,"11388":1,"11389":3}"""));
        using var _ = http;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AssembleAsync(2));
        Assert.DoesNotContain(handler.Requests, r => r.AbsolutePath == "/inv_use.php");
    }

    [Fact]
    public async Task Assemble_stops_at_an_unchanged_inventory_and_does_not_retry()
    {
        var (client, handler, http) = Create(
            Inv("""{"11386":2,"11387":2,"11388":2,"11389":2}"""),
            Html("<html>You can't do that right now.</html>"),
            Inv("""{"11386":2,"11387":2,"11388":2,"11389":2}"""));
        using var _ = http;

        var result = await client.AssembleAsync(2);

        Assert.False(result.Complete);
        Assert.False(result.Unclear);
        Assert.Equal(0, result.Confirmed);
        Assert.Contains("unchanged", result.Problem);
        Assert.Single(handler.Requests, r => r.AbsolutePath == "/inv_use.php");
    }

    [Fact]
    public async Task Assemble_reports_an_unexpected_delta_as_unclear_and_stops()
    {
        var (client, handler, http) = Create(
            Inv("""{"11386":2,"11387":2,"11388":2,"11389":2}"""),
            Html(AssemblePage),
            Inv("""{"11386":2,"11387":1,"11388":1,"11389":1,"11473":1}"""));
        using var _ = http;

        var result = await client.AssembleAsync(2);

        Assert.True(result.Unclear);
        Assert.Equal(0, result.Confirmed);
        Assert.Single(handler.Requests, r => r.AbsolutePath == "/inv_use.php");
    }

    [Fact]
    public async Task Assemble_is_unclear_when_the_inventory_cannot_be_read_after_sending()
    {
        var (client, handler, http) = Create(
            Inv("""{"11386":1,"11387":1,"11388":1,"11389":1}"""),
            Html(AssemblePage),
            _ => throw new HttpRequestException("boom"));
        using var _ = http;

        var result = await client.AssembleAsync(1);

        Assert.True(result.Unclear);
        Assert.Equal(0, result.Confirmed);
        Assert.Contains("Check", result.Problem);
    }

    [Fact]
    public async Task Rare_options_use_the_key_follow_the_one_time_message_once_then_read_main()
    {
        var (client, handler, http) = Create(
            Html("<html><body>The Kingdom of Loathing main map</body></html>"),
            Inv("""{"11473":1}"""),
            Redirect(MessageUrl),
            Redirect("main.php"), // the message page was already consumed: KoL sends main.php
            Html(KeyFormPage));
        using var _ = http;

        var form = await client.GetRareOptionsAsync();

        Assert.Equal(28, form.Options.Count);
        var paths = handler.Requests.Select(r => r.PathAndQuery).ToList();
        Assert.Equal(new[]
        {
            "/main.php", "/api.php?what=inventory&for=KoLibri", "/inv_use.php?whichitem=11473&pwd=fixture-pwd",
            "/inventory.php?which=&action=message", "/main.php"
        }, paths);
    }

    [Fact]
    public async Task Rare_options_come_from_an_open_form_without_using_the_key()
    {
        var (client, handler, http) = Create(Html(KeyFormPage));
        using var _ = http;
        var form = await client.GetRareOptionsAsync();
        Assert.Equal(28, form.Options.Count);
        Assert.Equal("/main.php", Assert.Single(handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task Redeem_sends_one_request_per_key_follows_the_message_once_and_confirms_by_delta()
    {
        var (client, handler, http) = Create(
            Inv("""{"11473":1}"""),
            Html("<html><body>main map</body></html>"),
            Redirect(MessageUrl),
            Html(KeyFormPage), // the use's one-time message page carries the form
            Redirect(MessageUrl),
            Html("<html><body>You acquire an item: <b>Elf Guard Field Manual: Extortion</b></body></html>"),
            Inv("""{"11449":1}"""));
        using var _ = http;

        var result = await client.RedeemAsync(11449, 1);

        Assert.True(result.Complete);
        Assert.Contains("Extortion", result.Steps[0].Message);
        Assert.Equal(-1, result.Steps[0].InventoryDelta[PirateKeyClient.CompleteKey]);
        Assert.Equal(1, result.Steps[0].InventoryDelta[11449]);
        var redeem = Assert.Single(handler.Requests, r => r.Query.Contains("rare="));
        Assert.Equal("/inv_use.php?whichitem=11473&qty=1&pwd=fixture-pwd&rare=11449", redeem.PathAndQuery);
        Assert.Equal(2, handler.Requests.Count(r => r.PathAndQuery == "/" + MessageUrl));
    }

    [Fact]
    public async Task Redeem_with_unknown_rare_id_is_refused_before_any_request()
    {
        var (client, handler, http) = Create();
        using var _ = http;
        await Assert.ThrowsAsync<ArgumentException>(() => client.RedeemAsync(12345, 1));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Redeem_stops_when_the_live_form_does_not_offer_the_item()
    {
        var withoutExtortion = KeyFormPage.Replace("<option value='11449'>Elf Guard Field Manual: Extortion</option>", "");
        var (client, handler, http) = Create(Inv("""{"11473":2}"""), Html(withoutExtortion));
        using var _ = http;

        var result = await client.RedeemAsync(11449, 2);

        Assert.False(result.Complete);
        Assert.False(result.Unclear);
        Assert.Contains("does not offer", result.Problem);
        Assert.DoesNotContain(handler.Requests, r => r.Query.Contains("rare="));
    }

    [Fact]
    public async Task Redeem_refuses_more_keys_than_held()
    {
        var (client, handler, http) = Create(Inv("""{"11473":1}"""));
        using var _ = http;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RedeemAsync(11449, 2));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Redirect_to_another_host_is_not_followed()
    {
        var (client, handler, http) = Create(
            Inv("""{"11386":1,"11387":1,"11388":1,"11389":1}"""),
            r =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = r, Content = new StringContent("") };
                response.Headers.Location = new Uri("https://evil.example/steal");
                return response;
            });
        using var _ = http;
        var result = await client.AssembleAsync(1);
        Assert.True(result.Unclear);
        Assert.DoesNotContain(handler.Requests, r => r.Host == "evil.example");
    }

    // ---- shop add ----

    private static OwnShopListing Listing(int id, long price, int qty) => new(id.ToString(), price, qty, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Shop_verify_needs_the_exact_quantity_at_the_price()
    {
        var before = new[] { Listing(11449, 5000, 2) };
        Assert.Null(ShopStocker.Verify(before, new[] { Listing(11449, 5000, 5) }, 11449, 3, 5000));
        Assert.NotNull(ShopStocker.Verify(before, new[] { Listing(11449, 5000, 4) }, 11449, 3, 5000));
        Assert.NotNull(ShopStocker.Verify(Array.Empty<OwnShopListing>(), Array.Empty<OwnShopListing>(), 11449, 1, 5000));
        Assert.NotNull(ShopStocker.CheckBefore(before, 11449, 4000));
        Assert.Null(ShopStocker.CheckBefore(before, 11449, 5000));
    }

    [Fact]
    public async Task Shop_add_posts_additem_and_confirms_by_reading_the_store_back()
    {
        var reads = new Queue<IReadOnlyList<OwnShopListing>>(new[]
        {
            (IReadOnlyList<OwnShopListing>)Array.Empty<OwnShopListing>(),
            new[] { Listing(11449, 7000, 3) }
        });
        var handler = new ScriptedHandler(
            r => Ok(r, """<input type="hidden" name="pwd" value="store-pwd">"""),
            r => Ok(r, "<html>stocked</html>"));
        using var http = new HttpClient(handler);
        var lister = new StoreLister(http, new Uri("https://fixture.test/"));
        var credentials = new AccountCredentials { CookieHeader = "PHPSESSID=x" };

        var outcome = await ShopStocker.AddAsync(lister, credentials, _ => Task.FromResult(reads.Dequeue()), 11449, 3, 7000, 1);

        Assert.True(outcome.Confirmed);
        Assert.Equal(3, outcome.QuantityAfter);
        Assert.Equal("/backoffice.php?which=1", handler.Requests[0].PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.Methods[1]);
        Assert.Contains("action=additem", handler.Bodies[1]);
        Assert.Contains("itemid=11449", handler.Bodies[1]);
        Assert.Contains("quantity=3", handler.Bodies[1]);
        Assert.Contains("price=7000", handler.Bodies[1]);
        Assert.Contains("limit=1", handler.Bodies[1]);
    }

    [Fact]
    public async Task Shop_add_refuses_an_item_listed_at_another_price_without_sending()
    {
        var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var lister = new StoreLister(http, new Uri("https://fixture.test/"));
        IReadOnlyList<OwnShopListing> shop = new[] { Listing(11449, 9000, 1) };

        var outcome = await ShopStocker.AddAsync(lister, new AccountCredentials { CookieHeader = "x" }, _ => Task.FromResult(shop), 11449, 1, 7000);

        Assert.False(outcome.Sent);
        Assert.Empty(handler.Requests);
    }

    // ---- CLI previews ----

    private static async Task<(int Code, string Out)> RunCli(params string[] args)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { return (await KolibriCli.RunAsync(args, _ => null), writer.ToString()); }
        finally { Console.SetOut(original); }
    }

    [Theory]
    [InlineData("keys", "assemble", "--role", "main", "--count", "3")]
    [InlineData("keys", "redeem", "--role", "main", "--item", "whalegun", "--count", "2")]
    [InlineData("shop", "add", "--role", "mallbot", "--item", "11431", "--qty", "2", "--price", "50000")]
    public async Task Cli_previews_send_nothing(params string[] args)
    {
        // A preview opens no session: it runs without any stored login and returns previewOnly.
        var (code, output) = await RunCli(args);
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("previewOnly").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("executed").GetBoolean());
    }

    [Fact]
    public async Task Cli_redeem_with_unknown_item_fails_before_anything_is_opened()
    {
        var (code, output) = await RunCli("keys", "redeem", "--role", "main", "--item", "seal tooth", "--count", "1");
        Assert.NotEqual(0, code);
        Assert.Equal("", output.Trim());
    }

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;
        public List<Uri> Requests { get; } = new();
        public List<HttpMethod> Methods { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Methods.Add(request.Method);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_next >= script.Length)
                throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
            return script[_next++](request);
        }
    }
}
