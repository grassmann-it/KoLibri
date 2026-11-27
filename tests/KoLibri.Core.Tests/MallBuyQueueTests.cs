using KoLibri.Core.Markt;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Core's mall receipt reader and diagnostics. The desktop buy queue tests live in KoLibri.Tests/Desktop.</summary>
public sealed class MallBuyQueueTests
{
    [Fact]
    public void Receipt_reader_follows_KoLmafias_markers()
    {
        Assert.Equal(MallReceiptKind.Confirmed, MallPurchaseReceiptReader.Read("<center><table><tr><td><table class=\"item\" rel=\"x\"><tr><td>You acquire an item</td></tr></table>You spent 222 Meat.</td></tr></table>").Kind);
        Assert.Equal(MallReceiptKind.Refused, MallPurchaseReceiptReader.Read("<center>This store doesn't have that item at that price.</center>").Kind);
        Assert.Equal(MallReceiptKind.Refused, MallPurchaseReceiptReader.Read("You can't afford that item.").Kind);
        Assert.Equal(MallReceiptKind.Unknown, MallPurchaseReceiptReader.Read("<html>Something else entirely</html>").Kind);
        Assert.Equal(MallReceiptKind.Unknown, MallPurchaseReceiptReader.Read("").Kind);
    }

    [Fact]
    public void Diagnostics_snippet_strips_tags_and_secrets_and_is_short()
    {
        var body = "<script>var pwd='abcdef0123456789abcdef0123456789';</script><b>Hello</b> pwd=secretvalue world " + new string('x', 600);
        var snippet = MallPurchaseDiagnostics.Snippet(body, 300);
        Assert.DoesNotContain("secretvalue", snippet);
        Assert.DoesNotContain("<b>", snippet);
        Assert.DoesNotContain("abcdef0123456789abcdef0123456789", snippet);
        Assert.StartsWith("Hello", snippet);
        Assert.True(snippet.Length <= 301);
    }
}
