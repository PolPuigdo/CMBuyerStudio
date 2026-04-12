using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using CMBuyerStudio.Infrastructure.Options;

namespace CMBuyerStudio.Tests.Unit;

public sealed class UrlBuilderTests
{
    [Fact]
    public void SearchUrl_ThrowsForBlankQuery()
    {
        Assert.Throws<ArgumentException>(() => UrlBuilder.SearchUrl("   "));
    }

    [Fact]
    public void SearchUrl_EncodesAndTrimsQuery()
    {
        var result = UrlBuilder.SearchUrl("  Lightning Bolt  ", 95);

        Assert.Contains("idExpansion=95", result);
        Assert.Contains("searchString=Lightning+Bolt", result);
        Assert.StartsWith("https://www.cardmarket.com/en/Magic/Products/Search", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFilteredCardUrl_ForcesSpanishLocaleAndConfiguredFilters()
    {
        var options = new ScrapingOptions
        {
            SellerCountry = "1,2",
            Languages = "1,7",
            MinCondition = 3
        };

        var result = UrlBuilder.BuildFilteredCardUrl(
            "https://www.cardmarket.com/en/Magic/Products/Singles/Alpha/Lightning-Bolt?foo=bar",
            options);

        Assert.StartsWith("https://www.cardmarket.com/es/", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("foo=bar", result);
        Assert.Contains("sellerCountry=1%2C2", result);
        Assert.Contains("language=1%2C7", result);
        Assert.Contains("minCondition=3", result);
    }
}
