using System.Collections.ObjectModel;
using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Common.Options;
using CMBuyerStudio.Application.Enums;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Application.Optimization;
using CMBuyerStudio.Application.RunAnalysis;
using CMBuyerStudio.Application.Services;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Domain.WantedCards;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CMBuyerStudio.Tests.Unit;

public sealed class RunAnalysisServiceTests
{
    [Fact]
    public async Task RunAsync_GeneratesEuAndLocalReportsAndPublishesProgressEvents()
    {
        const string productUrl = "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Lightning-Bolt";

        var wantedCardsRepository = new StubWantedCardsRepository(
        [
            new WantedCardGroup
            {
                CardName = "Lightning Bolt",
                DesiredQuantity = 1,
                Variants =
                [
                    new WantedCardVariant
                    {
                        SetName = "Set A",
                        ProductUrl = productUrl
                    }
                ]
            }
        ]);
        var marketData = new MarketCardData
        {
            Target = new ScrapingTarget
            {
                CardName = "Lightning Bolt",
                SetName = "Set A",
                ProductUrl = productUrl,
                DesiredQuantity = 1
            },
            ScrapedAtUtc = DateTime.UtcNow,
            Offers =
            [
                new SellerOffer
                {
                    SellerName = "MagicBarcelona",
                    Country = "Spain",
                    Price = 0.60m,
                    AvailableQuantity = 1,
                    CardName = "Lightning Bolt",
                    SetName = "Set A",
                    ProductUrl = productUrl
                },
                new SellerOffer
                {
                    SellerName = "Kashu",
                    Country = "Romania",
                    Price = 0.10m,
                    AvailableQuantity = 1,
                    CardName = "Lightning Bolt",
                    SetName = "Set A",
                    ProductUrl = productUrl
                }
            ]
        };
        var cacheService = new StubMarketDataCacheService([marketData]);
        var reportGenerator = new RecordingHtmlReportGenerator();
        var progressCollector = new ProgressCollector();
        using var appPaths = new TestAppPaths();
        var sut = new RunAnalysisService(
            wantedCardsRepository,
            cacheService,
            new EmptyCardMarketScraper(),
            reportGenerator,
            new OfferPurger(),
            new PurchaseOptimizer(Options.Create(new PurchaseOptimizerOptions())),
            appPaths,
            Options.Create(new ShippingCostsOptions
            {
                Default = 3.0,
                Countries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Spain"] = 1.45,
                    ["Romania"] = 3.00
                }
            }));

        await sut.RunAsync(progressCollector);

        Assert.Equal(2, reportGenerator.Requests.Count);
        Assert.Equal(
            [SellerScopeMode.Eu, SellerScopeMode.Local],
            reportGenerator.Requests.Select(request => request.Scope).ToArray());
        Assert.True(reportGenerator.Requests[0].GeneratedAt == reportGenerator.Requests[1].GeneratedAt);

        var reportEvents = progressCollector.Events.OfType<ReportGeneratedEvent>().ToList();
        Assert.Equal(2, reportEvents.Count);
        Assert.Contains(reportEvents, reportEvent => reportEvent.Scope == "EU" && reportEvent.Path.EndsWith("eu.html", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reportEvents, reportEvent => reportEvent.Scope == "Local" && reportEvent.Path.EndsWith("local.html", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progressCollector.Events, e => e is CalculationProfileSnapshotEvent { Scope: "Setup" });
        Assert.Contains(progressCollector.Events, e => e is CalculationProfileCompletedEvent { Scope: "EU" });
        Assert.Contains(progressCollector.Events, e => e is CalculationProfileCompletedEvent { Scope: "Local" });
        Assert.Single(Directory.GetFiles(appPaths.LogsPath, "best-seller-profile-*.json"));
        Assert.IsType<RunCompletedEvent>(progressCollector.Events[^1]);
    }

    [Fact]
    public async Task RunAsync_AggregatesVariantsIntoSingleLogicalCardBeforeOptimization()
    {
        const string variantAUrl = "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Lightning-Bolt";
        const string variantBUrl = "https://www.cardmarket.com/en/Magic/Products/Singles/Set-B/Lightning-Bolt";

        var wantedCardsRepository = new StubWantedCardsRepository(
        [
            new WantedCardGroup
            {
                CardName = "Lightning Bolt",
                DesiredQuantity = 2,
                Variants =
                [
                    new WantedCardVariant
                    {
                        SetName = "Set A",
                        ProductUrl = variantAUrl
                    },
                    new WantedCardVariant
                    {
                        SetName = "Set B",
                        ProductUrl = variantBUrl
                    }
                ]
            }
        ]);
        var cacheService = new StubMarketDataCacheService(
        [
            MarketData(
                "Lightning Bolt",
                "Set A",
                variantAUrl,
                99,
                Offer("MixSeller", "Spain", 0.40m, 1, "Lightning Bolt", "Set A", variantAUrl)),
            MarketData(
                "Lightning Bolt",
                "Set B",
                variantBUrl,
                99,
                Offer("MixSeller", "Spain", 0.50m, 1, "Lightning Bolt", "Set B", variantBUrl))
        ]);
        var reportGenerator = new RecordingHtmlReportGenerator();
        using var appPaths = new TestAppPaths();
        var sut = new RunAnalysisService(
            wantedCardsRepository,
            cacheService,
            new EmptyCardMarketScraper(),
            reportGenerator,
            new OfferPurger(),
            new PurchaseOptimizer(Options.Create(new PurchaseOptimizerOptions())),
            appPaths,
            Options.Create(new ShippingCostsOptions
            {
                Default = 3.0,
                Countries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Spain"] = 1.45
                }
            }));

        await sut.RunAsync(new ProgressCollector());

        var euRequest = Assert.Single(reportGenerator.Requests.Where(request => request.Scope == SellerScopeMode.Eu));
        var scopedCard = Assert.Single(euRequest.Snapshot.ScopedMarketData);
        Assert.Equal("Lightning Bolt", scopedCard.Target.RequestKey);
        Assert.Equal("Lightning Bolt", scopedCard.Target.CardName);
        Assert.Equal(2, scopedCard.Target.DesiredQuantity);
        Assert.Equal(2, scopedCard.Offers.Count);

        Assert.Equal(["MixSeller"], euRequest.OptimizationResult.SelectedSellerNames);
        Assert.Equal(2, euRequest.OptimizationResult.Assignments.Sum(assignment => assignment.Quantity));
        Assert.Contains(euRequest.OptimizationResult.Assignments, assignment => assignment.ProductUrl == variantAUrl);
        Assert.Contains(euRequest.OptimizationResult.Assignments, assignment => assignment.ProductUrl == variantBUrl);
    }

    [Fact]
    public async Task RunAsync_CompactsEquivalentOffersAndPersistsProfiles()
    {
        const string variantAUrl = "https://www.cardmarket.com/en/Magic/Products/Singles/Set-A/Lightning-Bolt";
        const string variantBUrl = "https://www.cardmarket.com/en/Magic/Products/Singles/Set-B/Lightning-Bolt";

        var wantedCardsRepository = new StubWantedCardsRepository(
        [
            new WantedCardGroup
            {
                CardName = "Lightning Bolt",
                DesiredQuantity = 2,
                Variants =
                [
                    new WantedCardVariant { SetName = "Set A", ProductUrl = variantAUrl },
                    new WantedCardVariant { SetName = "Set B", ProductUrl = variantBUrl }
                ]
            }
        ]);
        var cacheService = new StubMarketDataCacheService(
        [
            MarketData(
                "Lightning Bolt",
                "Set A",
                variantAUrl,
                2,
                Offer("TrimSeller", "Spain", 0.40m, 1, "Lightning Bolt", "Set A", variantAUrl),
                Offer("TrimSeller", "Spain", 0.40m, 1, "Lightning Bolt", "Set A", variantAUrl),
                Offer("TrimSeller", "Spain", 0.90m, 5, "Lightning Bolt", "Set A", variantAUrl)),
            MarketData(
                "Lightning Bolt",
                "Set B",
                variantBUrl,
                2,
                Offer("OtherSeller", "Spain", 0.55m, 1, "Lightning Bolt", "Set B", variantBUrl))
        ]);
        var reportGenerator = new RecordingHtmlReportGenerator();
        var progressCollector = new ProgressCollector();
        using var appPaths = new TestAppPaths();
        var sut = new RunAnalysisService(
            wantedCardsRepository,
            cacheService,
            new EmptyCardMarketScraper(),
            reportGenerator,
            new OfferPurger(),
            new PurchaseOptimizer(Options.Create(new PurchaseOptimizerOptions())),
            appPaths,
            Options.Create(new ShippingCostsOptions
            {
                Default = 3.0,
                Countries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Spain"] = 1.45
                }
            }));

        await sut.RunAsync(progressCollector);

        var euRequest = Assert.Single(reportGenerator.Requests.Where(request => request.Scope == SellerScopeMode.Eu));
        var scopedCard = Assert.Single(euRequest.Snapshot.ScopedMarketData);
        Assert.Equal(2, scopedCard.Offers.Count);
        Assert.Contains(scopedCard.Offers, offer => offer.SellerName == "TrimSeller" && offer.Price == 0.40m && offer.AvailableQuantity == 2);
        Assert.Contains(scopedCard.Offers, offer => offer.SellerName == "OtherSeller" && offer.Price == 0.55m && offer.AvailableQuantity == 1);
        Assert.DoesNotContain(scopedCard.Offers, offer => offer.SellerName == "TrimSeller" && offer.Price == 0.90m);

        var profilePath = Assert.Single(Directory.GetFiles(appPaths.LogsPath, "best-seller-profile-*.json"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        var profiles = document.RootElement.GetProperty("profiles");
        Assert.Equal(2, profiles.GetArrayLength());
        Assert.Contains(progressCollector.Events, e => e is CalculationProfileSnapshotEvent { Scope: "Setup" });
        Assert.Contains(progressCollector.Events, e => e is CalculationProfileCompletedEvent { Scope: "EU" });
    }

    private static MarketCardData MarketData(
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

    private sealed class StubWantedCardsRepository : IWantedCardsRepository
    {
        private readonly IReadOnlyList<WantedCardGroup> _groups;

        public StubWantedCardsRepository(IReadOnlyList<WantedCardGroup> groups)
        {
            _groups = groups;
        }

        public Task<IReadOnlyList<WantedCardGroup>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_groups);

        public Task SaveAllAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubMarketDataCacheService : IMarketDataCacheService
    {
        private readonly Dictionary<string, MarketCardData> _marketDataByUrl;

        public StubMarketDataCacheService(IEnumerable<MarketCardData> marketData)
        {
            _marketDataByUrl = marketData.ToDictionary(
                item => item.Target.ProductUrl,
                item => item,
                StringComparer.OrdinalIgnoreCase);
        }

        public Task<MarketCardData?> GetAsync(ScrapingTarget target, CancellationToken cancellationToken = default)
        {
            _marketDataByUrl.TryGetValue(target.ProductUrl, out var marketData);
            return Task.FromResult(marketData);
        }

        public Task SaveAsync(MarketCardData marketData, CancellationToken cancellationToken = default)
        {
            _marketDataByUrl[marketData.Target.ProductUrl] = marketData;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyCardMarketScraper : ICardMarketScraper
    {
        public Task<MarketCardData> ScrapeAsync(ScrapingTarget target, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<MarketCardData> ScrapeManyAsync(
            IEnumerable<ScrapingTarget> targets,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingHtmlReportGenerator : IHtmlReportGenerator
    {
        public List<HtmlReportRequest> Requests { get; } = [];

        public Task<GeneratedHtmlReport> GenerateAsync(HtmlReportRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new GeneratedHtmlReport
            {
                Scope = request.Scope,
                Path = request.Scope == SellerScopeMode.Eu
                    ? @"C:\Reports\best-seller-result-20260402-163104-eu.html"
                    : @"C:\Reports\best-seller-result-20260402-163104-local.html"
            });
        }
    }

    private sealed class ProgressCollector : IProgress<RunProgressEvent>
    {
        public List<RunProgressEvent> Events { get; } = [];

        public void Report(RunProgressEvent value)
        {
            Events.Add(value);
        }
    }

    private sealed class TestAppPaths : IAppPaths, IDisposable
    {
        private readonly string _rootPath = Path.Combine(
            Path.GetTempPath(),
            $"RunAnalysisServiceTests-{Guid.NewGuid():N}");

        public TestAppPaths()
        {
            Directory.CreateDirectory(CardsPath);
            Directory.CreateDirectory(CachePath);
            Directory.CreateDirectory(ReportsPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CardsCachePath);
            Directory.CreateDirectory(ImageCardsPath);
        }

        public string CardsPath => Path.Combine(_rootPath, "Cards");
        public string CachePath => Path.Combine(_rootPath, "Cache");
        public string ReportsPath => Path.Combine(_rootPath, "Reports");
        public string LogsPath => Path.Combine(_rootPath, "Logs");
        public string CardsCachePath => Path.Combine(_rootPath, "CardsCache");
        public string ImageCardsPath => Path.Combine(_rootPath, "Images");

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }
}
