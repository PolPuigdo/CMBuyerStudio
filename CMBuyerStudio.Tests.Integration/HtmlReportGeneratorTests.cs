using CMBuyerStudio.Application.Enums;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Reporting;
using CMBuyerStudio.Tests.Integration.Testing;

namespace CMBuyerStudio.Tests.Integration;

public sealed class HtmlReportGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_WritesPartiallyCoveredReportWithSetOptionsAndCardsWithoutOffers()
    {
        using var paths = new TestAppPaths();
        var sut = new HtmlReportGenerator(paths);
        var request = new HtmlReportRequest
        {
            Scope = SellerScopeMode.Local,
            GeneratedAt = new DateTimeOffset(2026, 04, 02, 16, 36, 02, TimeSpan.FromHours(2)),
            OptimizationResult = new PurchaseOptimizationResult
            {
                SelectedSellerNames = ["MagicBarcelona"],
                Assignments =
                [
                    new PurchaseAssignment(
                        SellerName: "MagicBarcelona",
                        ProductUrl: "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Lightning-Bolt",
                        CardName: "Lightning Bolt",
                        SetName: "Set A",
                        Quantity: 1,
                        UnitPrice: 0.15m,
                        TotalPrice: 0.15m)
                ],
                SellerCount = 1,
                CardsTotalPrice = 0.15m,
                UncoveredCardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "https://www.cardmarket.com/en/Magic/Products/Singles/Set-C/Fatal-Push"
                }
            },
            Snapshot = new PurgedScopeSnapshot
            {
                ScopedMarketData =
                [
                    Card(
                        "Lightning Bolt",
                        "Set A",
                        "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Lightning-Bolt",
                        1,
                        Offer("MagicBarcelona", "Spain", 0.15m, 1, "Lightning Bolt", "Set A", "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Lightning-Bolt")),
                    Card(
                        "Lightning Bolt",
                        "Set B",
                        "https://www.cardmarket.com/en/Magic/Products/Singles/Set-B/Lightning-Bolt",
                        1,
                        Offer("MagicBarcelona", "Spain", 0.25m, 1, "Lightning Bolt", "Set B", "https://www.cardmarket.com/en/Magic/Products/Singles/Set-B/Lightning-Bolt")),
                    Card(
                        "Fatal Push",
                        "Set C",
                        "https://www.cardmarket.com/en/Magic/Products/Singles/Set-C/Fatal-Push",
                        1)
                ],
                PurgedMarketData = [],
                FixedCostBySellerName = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MagicBarcelona"] = 1.45m
                },
                UncoveredCardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "https://www.cardmarket.com/en/Magic/Products/Singles/Set-C/Fatal-Push"
                }
            }
        };

        var report = await sut.GenerateAsync(request);
        var html = await File.ReadAllTextAsync(report.Path);

        Assert.True(File.Exists(report.Path));
        Assert.Contains("Partially Covered", html);
        Assert.Contains("MagicBarcelona", html);
        Assert.Contains("Available Set Options", html);
        Assert.Contains("Set B", html);
        Assert.Contains("Fatal Push", html);
        Assert.Contains("N/A", html);
        Assert.Contains("Source 1", html);
        Assert.Contains("https://www.cardmarket.com/es/Magic/Users/MagicBarcelona", html);
    }

    [Fact]
    public async Task GenerateAsync_WritesFullyCoveredReportWithoutUncoveredSection()
    {
        using var paths = new TestAppPaths();
        var sut = new HtmlReportGenerator(paths);
        var request = new HtmlReportRequest
        {
            Scope = SellerScopeMode.Eu,
            GeneratedAt = new DateTimeOffset(2026, 04, 02, 16, 34, 07, TimeSpan.FromHours(2)),
            OptimizationResult = new PurchaseOptimizationResult
            {
                SelectedSellerNames = ["Kashu"],
                Assignments =
                [
                    new PurchaseAssignment(
                        SellerName: "Kashu",
                        ProductUrl: "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Terminate",
                        CardName: "Terminate",
                        SetName: "Set A",
                        Quantity: 2,
                        UnitPrice: 0.19m,
                        TotalPrice: 0.38m)
                ],
                SellerCount = 1,
                CardsTotalPrice = 0.38m,
                UncoveredCardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            },
            Snapshot = new PurgedScopeSnapshot
            {
                ScopedMarketData =
                [
                    Card(
                        "Terminate",
                        "Set A",
                        "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Terminate",
                        2,
                        Offer("Kashu", "Romania", 0.19m, 2, "Terminate", "Set A", "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Terminate"))
                ],
                PurgedMarketData = [],
                FixedCostBySellerName = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Kashu"] = 3.00m
                },
                UncoveredCardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            }
        };

        var report = await sut.GenerateAsync(request);
        var html = await File.ReadAllTextAsync(report.Path);

        Assert.Contains("Fully Covered", html);
        Assert.DoesNotContain("<h2 style=\"margin-top:14px;\">Uncovered Cards</h2>", html);
        Assert.Contains("Kashu", html);
        Assert.Contains("3.38 EUR", html);
    }

    private static MarketCardData Card(
        string cardName,
        string setName,
        string productUrl,
        int desiredQuantity,
        params SellerOffer[] offers)
    {
        return new MarketCardData
        {
            Target = new ScrapingTarget
            {
                CardName = cardName,
                SetName = setName,
                ProductUrl = productUrl,
                DesiredQuantity = desiredQuantity
            },
            ScrapedAtUtc = DateTime.UtcNow,
            Offers = offers
        };
    }

    private static SellerOffer Offer(
        string sellerName,
        string country,
        decimal price,
        int availableQuantity,
        string cardName,
        string setName,
        string productUrl)
    {
        return new SellerOffer
        {
            SellerName = sellerName,
            Country = country,
            Price = price,
            AvailableQuantity = availableQuantity,
            CardName = cardName,
            SetName = setName,
            ProductUrl = productUrl
        };
    }
}
