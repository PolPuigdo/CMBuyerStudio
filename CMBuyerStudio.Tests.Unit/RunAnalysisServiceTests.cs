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
        var sut = new RunAnalysisService(
            wantedCardsRepository,
            cacheService,
            new EmptyCardMarketScraper(),
            reportGenerator,
            new OfferPurger(),
            new PurchaseOptimizer(Options.Create(new PurchaseOptimizerOptions())),
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
        Assert.IsType<RunCompletedEvent>(progressCollector.Events[^1]);
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
}
