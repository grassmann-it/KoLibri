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
}
