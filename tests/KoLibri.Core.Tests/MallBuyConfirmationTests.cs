using Xunit;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Tests;

public sealed class MallBuyConfirmationTests
{
    [Theory]
    [InlineData(200, "<b>You spent 25,000 Meat.</b> You acquire an item: <b>11-leaf clover</b>", true)]
    [InlineData(200, "You acquire an item: 11-leaf clover", true)]
    [InlineData(200, "This store doesn't have that item at that price.", false)]
    [InlineData(200, "", false)]
    [InlineData(302, "You acquire an item", false)]
    public void Only_a_confirmed_response_counts_as_a_purchase(int status, string body, bool expected)
        => Assert.Equal(expected, MallBuyClient.IsConfirmedPurchase(status, body));
}
