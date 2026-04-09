using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;

namespace CMBuyerStudio.Tests.Unit;

public sealed class PlaywrightParserTests
{
    private readonly PlaywrightParser _sut = new();

    [Fact]
    public void ParseSearchCardResultAsync_ReturnsParsedCard()
    {
        const string rowHtml = """
            <div data-testid='name'><a href='/es/Magic/Products/Singles/M11/Lightning-Bolt'>Lightning&nbsp;Bolt</a></div>
            <div data-testid='expansion'><a aria-label='Magic 2011'></a></div>
            <div data-testid='from_price'>0,80 €</div>
            <div data-testid='preview' data-bs-title="&lt;img src=&quot;//images.example/bolt.webp&quot; /&gt;"></div>
            """;

        var result = _sut.ParseSearchCardResultAsync(rowHtml, "https://www.cardmarket.com/es/Magic/Products/Singles?idExpansion=0");

        Assert.NotNull(result);
        Assert.Equal("Lightning Bolt", result!.CardName);
        Assert.Equal("Magic 2011", result.SetName);
        Assert.Equal("https://www.cardmarket.com/es/Magic/Products/Singles/M11/Lightning-Bolt", result.ProductUrl);
        Assert.Equal("https://images.example/bolt.webp", result.ImageUrl);
        Assert.Equal(0.80m, result.Price);
    }

    [Fact]
    public void ParseSearchCardResultAsync_ReturnsNullWhenNameOrPriceAreMissing()
    {
        const string rowHtml = "<div data-testid='name'><span>No link</span></div>";

        var result = _sut.ParseSearchCardResultAsync(rowHtml, "https://www.cardmarket.com/es/Magic");

        Assert.Null(result);
    }

    [Fact]
    public void ExtractImageUrlFromTooltipHtml_ReturnsEmptyWhenImageIsMissing()
    {
        var result = PlaywrightParser.ExtractImageUrlFromTooltipHtml("<div>No image</div>");

        Assert.Equal(string.Empty, result);
    }
}
