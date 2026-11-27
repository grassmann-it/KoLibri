using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class ChoiceTitleTests
{
    // Realistic choice page: scripts (containing <b> and a fake header cell) precede the real title.
    private const string Page = """
        <html><head><script language="Javascript" src="/images/scripts/jquery.js"></script>
        <script>var s = "<td bgcolor=blue><b>var x = 1;</b></td>"; function f(){ return '<b>garbage</b>'; }</script>
        <style>b { color: red; }</style></head><body>
        <center><table><tr><td style="color: white;" align=center bgcolor=blue><b>The Skeleton Store</b></td></tr>
        <tr><td><form action=choice.php><input type=hidden name=pwd value=XXXX><input type=hidden name=whichchoice value=1060>
        <input type=hidden name=option value=1><input class=button type=submit value="Talk to the clerk"></form></td></tr></table></center></body></html>
        """;

    [Fact]
    public void Title_is_the_blue_header_cell_not_script_content()
        => Assert.Equal("The Skeleton Store", KoLAdventureEncounterParser.ExtractChoiceTitle(Page));

    [Fact]
    public void Title_fallback_ignores_scripts()
        => Assert.Equal("Real", KoLAdventureEncounterParser.ExtractChoiceTitle(
            "<script>x='<b>junk</b>'</script><b>Real</b>"));

    // Current KoL layout (live 2026-11-27, KoLmafia test_choice_arch_spade.html): no bgcolor and a styled <b>; the page
    // had no plain <b> at all, so the title came out null.
    [Fact]
    public void Title_is_read_from_the_current_header_layout()
        => Assert.Equal("Temporarily Out of Skeletons", KoLAdventureEncounterParser.ExtractChoiceTitle("""
            <html><head><script>x='<b>Auto-Sell ('+i.s+' meat):</b>'</script></head><body><Center><centeR><table  width=95%  cellspacing=0 cellpadding=0>
            <tr><td style="background-color: blue" align=center ><b style="color: white">Temporarily Out of Skeletons</b></td></tr>
            <tr><td style="padding: 5px; border: 1px solid blue;"><p>You have <b>10</b> more tries.<form action=choice.php><input type=hidden name=whichchoice value=1060></form></td></tr></table></body></html>
            """));

    [Fact]
    public void A_blue_border_cell_is_not_the_header()
        => Assert.Equal("Real", KoLAdventureEncounterParser.ExtractChoiceTitle(
            """<td style="padding: 5px; border: 1px solid blue;"><b style="x">Real</b></td>"""));
}
