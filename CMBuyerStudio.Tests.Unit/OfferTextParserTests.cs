using CMBuyerStudio.Infrastructure.Cardmarket.Parsing;

namespace CMBuyerStudio.Tests.Unit;

public sealed class OfferTextParserTests
{
    [Fact]
    public void IsNonCertifiedShippingWarningText_DetectsKnownWarnings()
    {
        var result = OfferTextParser.IsNonCertifiedShippingWarningText("Envio sin certificar");

        Assert.True(result);
    }

    [Theory]
    [InlineData("Item location: Spain", "Spain")]
    [InlineData("Ubicación del artículo: Francia", "Francia")]
    [InlineData("Germany", "Germany")]
    public void ParseCountryFromLocationLabel_ExtractsCountry(string raw, string expected)
    {
        var result = OfferTextParser.ParseCountryFromLocationLabel(raw);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsLocationLabelPrefix_RecognizesSpanishPrefix()
    {
        Assert.True(OfferTextParser.IsLocationLabelPrefix("Ubicación del artículo"));
    }
}
