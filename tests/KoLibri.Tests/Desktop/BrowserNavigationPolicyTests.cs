using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>The in-app browser stays on the relay origin; the address bar never shows the token (moved from the Core relay tests).</summary>
public sealed class BrowserNavigationPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:9/main.php", BrowserNavigationDecision.Allow)]
    [InlineData("http://127.0.0.1:10/main.php", BrowserNavigationDecision.AskExternal)]
    [InlineData("https://www.kingdomofloathing.com/", BrowserNavigationDecision.AskExternal)]
    [InlineData("javascript:alert(1)", BrowserNavigationDecision.Block)]
    [InlineData("file:///etc/passwd", BrowserNavigationDecision.Block)]
    [InlineData("about:blank", BrowserNavigationDecision.Allow)]
    public void Navigation_policy_pins_the_view_to_the_relay_origin(string url, BrowserNavigationDecision expected)
        => Assert.Equal(expected, BrowserNavigationPolicy.Decide("http://127.0.0.1:9", url));

    [Fact]
    public void Address_bar_shows_paths_only_and_accepts_only_relay_paths()
    {
        const string o = "http://127.0.0.1:9";
        Assert.Equal("/", BrowserNavigationPolicy.DisplayPath(o, o + "/__kolibri/enter?t=SECRET&next=%2Fmain.php"));
        Assert.Equal("/a.php?pwd=…&action=x", BrowserNavigationPolicy.DisplayPath(o, o + "/a.php?pwd=abc&action=x"));
        Assert.Equal("", BrowserNavigationPolicy.DisplayPath(o, "https://example.com/"));
        Assert.Equal("/adventure.php?snarfblat=1", BrowserNavigationPolicy.ResolveTyped(o, "adventure.php?snarfblat=1"));
        Assert.Equal("/main.php", BrowserNavigationPolicy.ResolveTyped(o, o + "/main.php"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "https://example.com/x"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "//example.com"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "/__kolibri/enter?t=x"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "/../x"));
    }
}
