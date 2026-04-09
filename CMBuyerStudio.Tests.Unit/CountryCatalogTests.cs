using ApplicationCountryCatalog = CMBuyerStudio.Application.Common.Countries.CountryCatalog;
using InfrastructureCountryCatalog = CMBuyerStudio.Infrastructure.Cardmarket.Parsing.CountryCatalog;

namespace CMBuyerStudio.Tests.Unit;

public sealed class CountryCatalogTests
{
    [Theory]
    [InlineData("España", "ES")]
    [InlineData("Germany", "DE")]
    [InlineData("fr", "FR")]
    public void InfrastructureCountryCatalog_NormalizesAliases(string input, string expected)
    {
        var parsed = InfrastructureCountryCatalog.TryGetCountryCode(input, out var countryCode);

        Assert.True(parsed);
        Assert.Equal(expected, countryCode);
    }

    [Theory]
    [InlineData("España", "ES")]
    [InlineData("Netherlands", "NL")]
    [InlineData("DE", "DE")]
    public void ApplicationCountryCatalog_NormalizesAliases(string input, string expected)
    {
        var parsed = ApplicationCountryCatalog.TryGetCountryCode(input, out var countryCode);

        Assert.True(parsed);
        Assert.Equal(expected, countryCode);
    }

    [Fact]
    public void InfrastructureCountryCatalog_ToDisplayName_ReturnsFriendlyValue()
    {
        var displayName = InfrastructureCountryCatalog.ToDisplayName("ES");

        Assert.Equal("Spain", displayName);
    }
}
