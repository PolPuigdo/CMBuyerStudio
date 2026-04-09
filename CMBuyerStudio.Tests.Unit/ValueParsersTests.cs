using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;

namespace CMBuyerStudio.Tests.Unit;

public sealed class ValueParsersTests
{
    [Theory]
    [InlineData("1,23 €", 1.23)]
    [InlineData("1.234,56 €", 1234.56)]
    [InlineData("From 0,80 €", 0.80)]
    public void TryParseEuroPrice_ParsesEuropeanFormats(string raw, decimal expected)
    {
        var parsed = ValueParsers.TryParseEuroPrice(raw, out decimal price);

        Assert.True(parsed);
        Assert.Equal(expected, price);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N/A")]
    public void TryParseEuroPrice_ReturnsFalseForInvalidValues(string? raw)
    {
        var parsed = ValueParsers.TryParseEuroPrice(raw, out decimal _);

        Assert.False(parsed);
    }

    [Fact]
    public void BuildAbsoluteUrl_ResolvesRelativeUrls()
    {
        var result = ValueParsers.BuildAbsoluteUrl(
            "https://www.cardmarket.com/es/Magic/Products/Singles/Alpha/Lightning-Bolt",
            "/es/Magic/Products/Singles/M11/Lightning-Bolt");

        Assert.Equal("https://www.cardmarket.com/es/Magic/Products/Singles/M11/Lightning-Bolt", result);
    }
}
