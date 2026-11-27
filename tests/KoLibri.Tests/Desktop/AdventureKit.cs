using KoLibri.Desktop;
using KoLibri.Extras;

namespace KoLibri.Tests.Desktop;

/// <summary>Adventure tab against the fake KoL: recorded-style pages, a session view model, the vendor catalog.</summary>
internal static class AdventureKit
{
    public const string Base = "https://www.kingdomofloathing.com/";

    /// <summary>Unreadable pages are archived here, never in the user's log folder.</summary>
    public static readonly string UnknownPages = InitUnknownPages();

    private static string InitUnknownPages()
    {
        var dir = Directory.CreateTempSubdirectory("kolibri-unknown-pages-").FullName;
        AdventureUnknownPageArchive.DirectoryOverride = dir;
        return dir;
    }

    public static string CatalogDirectory => Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");

    public static EncounterBrowserViewModel Browser() => new(new MafiaDataLoader().Load(CatalogDirectory));

    /// <summary>Your Mushroom Garden (no access rule, so no quest reads before the start).</summary>
    public static readonly AdventureLocation Garden = new("adventure=543:Your Mushroom Garden", "Your Mushroom Garden", "Fixture", 543, 1);

    /// <summary>A fight page with HP, the skill and item dropdowns KoL shows, monster id 123.</summary>
    public const string Fight = """
        <html><body><span id="monname" monsterid="123">Skeleton</span>
        <span id="playerhp">80/100</span><span id="playermp">40/50</span>
        <form action="fight.php" method="post"><input type="hidden" name="onturn" value="1">
        <button name="action" value="attack">Attack</button></form>
        <form action="fight.php" method="post"><input type="hidden" name="action" value="skill">
        <select name=whichskill><option value="0">(select a skill)</option><option value="1001">Lunge Smack (3 Mojo Points)</option></select></form>
        <form action="fight.php" method="post"><input type="hidden" name="action" value="useitem">
        <select name=whichitem><option value="0">(select an item)</option><option value='2002'>dense meat stack (2)</option></select></form>
        </body></html>
        """;

    /// <summary>The same fight without player HP on the page (live 2026-11-26).</summary>
    public const string FightWithoutHp = """
        <html><body><span id="monname" monsterid="123">Skeleton</span>
        <form action="fight.php" method="post"><input type="hidden" name="onturn" value="2">
        <button name="action" value="attack">Attack</button></form></body></html>
        """;

    public const string FightWon = """
        <html><body><!--WINWINWIN--><p>You win the fight!</p>
        <p>You acquire an item: <b>knucklebone</b></p><p>You gain 15 Meat.</p></body></html>
        """;

    public const string FightLost = """
        <html><body><h1>You have been defeated!</h1><p>The fight is over.</p></body></html>
        """;

    public const string Choice = """
        <html><body><b>A Turtle</b>
        <form action="choice.php" method="post"><input type="hidden" name="whichchoice" value="1234">
        <input type="hidden" name="pwd" value="0123456789abcdef"><input type="hidden" name="option" value="1">
        <input class=button type="submit" value="Help the turtle"></form>
        <form action="choice.php" method="post"><input type="hidden" name="whichchoice" value="1234">
        <input type="hidden" name="pwd" value="0123456789abcdef"><input type="hidden" name="option" value="2">
        <input class=button type="submit" value="Leave it alone"></form></body></html>
        """;

    public const string ChoiceDone = """
        <html><body><b>Down the Road</b><p>You acquire an item: <b>knucklebone</b></p></body></html>
        """;

    public static HttpResponseMessage To(string page) => FakeKol.Redirect(Base + page);

    /// <summary>adventure.php → fight.php; every fight POST answers with <paramref name="fightPosts"/> in order (last repeats).</summary>
    public static FakeKol FightKol(params string[] fightPosts)
    {
        var kol = new FakeKol { StatusAdventures = 9, StatusHp = 80 };
        kol.On("POST adventure.php", (_, _) => To("fight.php")).On("GET fight.php", Fight);
        foreach (var page in fightPosts)
            kol.On("POST fight.php", (_, _) =>
            {
                if (page != Fight && page != FightWithoutHp) kol.StatusAdventures--;
                return FakeKol.Html(page);
            });
        return kol;
    }

    /// <summary>adventure.php → choice.php 1234; the choice POST ends the turn.</summary>
    public static FakeKol ChoiceKol()
    {
        var kol = new FakeKol { StatusAdventures = 9 };
        kol.On("POST adventure.php", (_, _) => To("choice.php")).On("GET choice.php", Choice)
           .On("POST choice.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(ChoiceDone); });
        return kol;
    }

    /// <summary>Main's session view model on the fake KoL; an empty choice policy (never the user's real file).</summary>
    public static EncounterBrowserSessionViewModel Session(FakeKol kol, string root, out DesktopAccounts accounts, AccountRole role = AccountRole.Main)
    {
        _ = UnknownPages;
        accounts = DesktopKit.Accounts(kol, DesktopKit.Store(role));
        return new EncounterBrowserSessionViewModel(accounts, new ApprovalTokenStore(root), role)
        {
            ApprovalToken = "fixture",
            ChoiceResolverFactory = () => ChoiceResolvers.AlwaysAsk
        };
    }

    public static IEnumerable<(string Path, string Form)> Posts(this FakeKol kol)
        => kol.Requests.Where(r => r.Method == "POST" && !r.Path.Contains("login")).Select(r => (r.Path.Split('?')[0], r.Form));

    public static Page PageOf(string path, string html, PageKind kind)
        => new(new Uri(Base + path), 200, html, kind, DateTimeOffset.UtcNow);

    public static ChoiceEncounter ChoiceEncounter(int id = 1003, string title = "Turtle Tamer")
        => new(PageOf("choice.php", "", PageKind.Choice), null, title, id,
            new[] { new ChoiceOption(1, "Tame it"), new ChoiceOption(2, "Leave") });

    public static FightEncounter FightEncounter(string html = Fight)
        => new(PageOf("fight.php", html, PageKind.Fight), null, "Skeleton", "Skeleton", 123, 1);

    /// <summary>Accounts on a handler that is not a plain FakeKol (e.g. <see cref="Blocking"/>), no limiter waits.</summary>
    public static DesktopAccounts Accounts(HttpMessageHandler handler, MemoryAccountStore store)
    {
        _ = UnknownPages;
        return new(new KolibriOptions
        {
            HttpHandler = handler,
            AccountStore = store,
            StateDirectory = Directory.CreateTempSubdirectory("kolibri-desktop-tests-").FullName,
            DataDirectory = CatalogDirectory,
            DisableLimiterForTests = true
        });
    }

    /// <summary>Both roles stored, without passwords (an expired cookie is never renewed by a login).</summary>
    public static MemoryAccountStore BothRoles()
    {
        var store = DesktopKit.Store(AccountRole.Main, password: null);
        store.SaveAsync(new AccountRecord(new Account(new AccountId(DesktopAccounts.IdOf(AccountRole.Mallbot)), "Bot", 1002, null, Array.Empty<string>()),
            "PHPSESSID=bot", null, DateTimeOffset.UtcNow.AddHours(3)), CancellationToken.None).Wait();
        return store;
    }
}

/// <summary>Wraps the fake KoL; requests to <paramref name="page"/> hang until they are cancelled (a slow KoL).</summary>
internal sealed class Blocking(FakeKol inner, string page) : DelegatingHandler(inner)
{
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.TrimStart('/').Equals(page, StringComparison.OrdinalIgnoreCase))
        {
            lock (inner.Requests) inner.Requests.Add((request.Method.Method, request.RequestUri.PathAndQuery, ""));
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
