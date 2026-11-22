using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class DesktopShellWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void LogKeepsNewestEntriesCollapsesRepeatsAndCountsErrors()
    {
        var log = new SessionLogViewModel(capacity: 10, now: () => Now);
        log.Search("a");
        log.Search("a");
        log.Error("boom");
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(1, log.ErrorCount);
        Assert.Contains("1 errors", log.Summary);
        for (var i = 0; i < 30; i++) log.Adventure($"turn {i}");
        Assert.Equal(10, log.Entries.Count);
        Assert.Equal(0, log.ErrorCount);
        Assert.Equal("turn 29", log.Entries[^1].Message);
        log.Add(SessionLogKind.Info, "  ");
        Assert.Equal(10, log.Entries.Count);
        log.Clear();
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void SettingsMapOntoPurchaseBudget()
    {
        var settings = new DesktopSettings();
        settings.Set("meatPerDay", 40_000);
        settings.Set("meatPerPurchase", 9_000);
        settings.Set("unitsPerPurchase", 3);
        var options = DesktopMallPurchaseSettings.ToOptions(settings);
        Assert.Equal(40_000, options.MaxDailySpend);
        Assert.Equal(9_000, options.MaxMeatPerPurchase);
        Assert.Equal(3, options.MaxQuantityPerPurchase);
    }

    private sealed class FakeReads(long meat, int owned) : IMallPurchaseReads
    {
        public int Searches;
        public Task<MallSearchPage> SearchAsync(string term, CancellationToken ct)
        {
            Searches++;
            var listing = new MallListing("789", "42", "Shop", 100, 5, null, Now, new Uri("https://www.kingdomofloathing.com/"))
            { SellerId = "42", QuantityKnown = true, LimitKind = MallLimitKind.Unlimited };
            return Task.FromResult(new MallSearchPage(term,
                [new MallItemOffers("789", "Thing", [listing], 1)], Now, new Uri("https://www.kingdomofloathing.com/")));
        }
        public Task<KoLStatusSnapshot?> StatusAsync(CancellationToken ct)
            => Task.FromResult<KoLStatusSnapshot?>(new KoLStatusSnapshot("Bot", 10, meat, 1, Now));
        public Task<DesktopInventorySnapshot> InventoryAsync(CancellationToken ct)
            => Task.FromResult(new DesktopInventorySnapshot
            {
                Role = AccountRole.Mallbot,
                Items = [new DesktopInventoryItem { ItemId = "789", Name = "Thing", Quantity = owned }],
                ObservedAt = Now
            });
    }

    [Fact]
    public async Task ObserverReadsOfferAndAccountAndTracksDailySpend()
    {
        var ledger = new MallSpendLedger(new FixedTime());
        ledger.Add("Mallbot", 1_000);
        var observer = new MallPurchaseObserver(new FakeReads(10_000, 2), "Mallbot", () => 50_000, ledger, new FixedTime());
        var request = new MallPurchaseRequest("42", "42", "789", 100, 2, accountId: "Mallbot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.BeforeAsync(request, default));
        observer.Remember("789", "Thing");

        var before = await observer.BeforeAsync(request, default);
        Assert.Equal(100, before.Offer.UnitPrice);
        Assert.Equal(5, before.Offer.Quantity);
        Assert.Equal(10_000, before.Account.Meat);
        Assert.Equal(1_000, before.Account.DailySpent);
        Assert.Equal(50_000, before.Account.DailyLimit);
        Assert.Equal(2, before.InventoryQuantity("789"));

        // After a purchase meat fell by 200: daily spend follows the observed change.
        var after = new MallPurchaseObserver(new FakeReads(9_800, 4), "Mallbot", () => 50_000, ledger, new FixedTime());
        after.Remember("789", "Thing");
        var preview = new MallPurchasePreview("a", request, before, Now, Now.AddMinutes(5), false, false);
        var observed = await after.AfterAsync(new MallPurchaseTransportCommit(preview, before, null, Now), default);
        Assert.Equal(1_200, observed.Account.DailySpent);
        Assert.Equal(4, observed.InventoryQuantity("789"));
    }

    private sealed class Prompt(bool confirm) : IMallPurchasePrompt
    {
        public int Asked;
        public int? Quantity;
        public async Task<DesktopMallPurchasePreview?> AskAsync(MallPurchasePromptContext context,
            Func<int, CancellationToken, Task<DesktopMallPurchasePreview>> preview, CancellationToken ct)
        {
            Asked++;
            var p = await preview(2, ct);
            Quantity = p.Quantity;
            return confirm ? p : null;
        }
    }

    private static MallOfferRow Row(int? stock = 5) => new()
    {
        Rank = 1, ItemId = "789", ItemName = "Thing", ShopId = "42", ShopName = "Shop",
        UnitPrice = 100, Stock = stock, LimitKind = MallLimitKind.Unlimited
    };

    private static (MallPurchaseCoordinator Coordinator, FixtureMallPurchaseTransport Fixture, SessionLogViewModel Log, MallSpendLedger Ledger)
        Build(Prompt prompt, bool signedIn = true)
    {
        var fixture = new FixtureMallPurchaseTransport(new MallPurchaseObservation(
            new MallPurchaseOffer("42", "42", "789", 100, 5, Now),
            new MallPurchaseAccountState("Mallbot", 10_000, 0, 1_000_000,
                new Dictionary<string, int> { ["789"] = 0 }, Now)));
        var log = new SessionLogViewModel(now: () => Now);
        var ledger = new MallSpendLedger(new FixedTime());
        var service = new DesktopMallPurchaseService(fixture, null, null, new FixedTime());
        var observer = new MallPurchaseObserver(new FakeReads(10_000, 0), "Mallbot", () => 1_000_000, ledger, new FixedTime());
        var coordinator = new MallPurchaseCoordinator("Mallbot",
            _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>(signedIn ? (service, observer) : null),
            prompt, log, ledger);
        return (coordinator, fixture, log, ledger);
    }

    [Fact]
    public async Task ConfirmedBuySendsOnePurchaseAndRecordsSpend()
    {
        var prompt = new Prompt(true);
        var (coordinator, fixture, log, ledger) = Build(prompt);
        Assert.True(coordinator.CanRequest(Row()));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(1, fixture.CommitCount);
        Assert.Equal(2, prompt.Quantity);
        Assert.Equal(200, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Kind == SessionLogKind.Purchase && e.Message.Contains("Purchased"));
    }

    [Fact]
    public async Task CancelledPromptSendsNothing()
    {
        var (coordinator, fixture, log, ledger) = Build(new Prompt(false));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, fixture.CommitCount);
        Assert.Equal(0, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Message.Contains("nothing was sent"));
    }

    [Fact]
    public async Task SignedOutAndSoldOutOffersDoNotBuy()
    {
        var prompt = new Prompt(true);
        var (coordinator, fixture, log, _) = Build(prompt, signedIn: false);
        Assert.False(coordinator.CanRequest(Row(stock: 0)));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, prompt.Asked);
        Assert.Equal(0, fixture.CommitCount);
        Assert.Equal(1, log.ErrorCount);
    }
}
