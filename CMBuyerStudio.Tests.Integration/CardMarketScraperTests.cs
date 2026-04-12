using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using CMBuyerStudio.Infrastructure.Cardmarket.Scraping;
using CMBuyerStudio.Tests.Integration.Testing;

namespace CMBuyerStudio.Tests.Integration;

public sealed class CardMarketScraperTests
{
    [Fact]
    public async Task ScrapeAsync_FiltersOffersAndStopsAtMaxPrice()
    {
        var fixtureHtml = FixtureLoader.LoadText("scrape-offers.html");
        var sessionFactory = new TestPlaywrightSessionFactory(_ => TestRouteResponse.Html(fixtureHtml));
        var sut = CreateScraper(
            sessionFactory,
            new NavigateOnlyCardmarketSessionSetup(),
            new NoDelayScrapeDelayStrategy());

        var result = await sut.ScrapeAsync(Target("https://www.cardmarket.com/en/Magic/Products/Singles/Alpha/Lightning-Bolt"));

        Assert.Equal(2, result.Offers.Count);
        Assert.Equal(["Seller Fallback", "Seller One"], result.Offers.Select(x => x.SellerName).OrderBy(x => x));
        Assert.Equal(4, result.Offers.Single(x => x.SellerName == "Seller One").AvailableQuantity);
        Assert.Equal(1, result.Offers.Single(x => x.SellerName == "Seller Fallback").AvailableQuantity);
        Assert.Equal("Italy", result.Offers.Single(x => x.SellerName == "Seller Fallback").Country);
    }

    [Fact]
    public async Task ScrapeManyAsync_RetriesUntilTargetEventuallySucceeds()
    {
        var fixtureHtml = FixtureLoader.LoadText("scrape-offers.html");
        var sessionFactory = new TestPlaywrightSessionFactory(_ => TestRouteResponse.Html(fixtureHtml));
        var setup = new ConfigurableCardmarketSessionSetup((_, attempt) =>
            attempt < 3 ? new InvalidOperationException("Transient failure") : null);
        var sut = CreateScraper(sessionFactory, setup, new NoDelayScrapeDelayStrategy());

        var results = await ToListAsync(sut.ScrapeManyAsync([Target("https://www.cardmarket.com/en/Magic/Products/Singles/Alpha/Lightning-Bolt")]));

        Assert.Single(results);
        Assert.Equal(2, results[0].Offers.Count);
    }

    [Fact]
    public async Task ScrapeManyAsync_CompletesWhenOneTargetExhaustsRetries()
    {
        var fixtureHtml = FixtureLoader.LoadText("scrape-offers.html");
        var sessionFactory = new TestPlaywrightSessionFactory(_ => TestRouteResponse.Html(fixtureHtml));
        var setup = new ConfigurableCardmarketSessionSetup((url, _) =>
            url.Contains("AlwaysFail", StringComparison.OrdinalIgnoreCase)
                ? new InvalidOperationException("Permanent failure")
                : null);
        var sut = CreateScraper(sessionFactory, setup, new NoDelayScrapeDelayStrategy());

        var results = await ToListAsync(sut.ScrapeManyAsync(
        [
            Target("https://www.cardmarket.com/en/Magic/Products/Singles/Alpha/AlwaysFail"),
            Target("https://www.cardmarket.com/en/Magic/Products/Singles/Alpha/Lightning-Bolt")
        ]));

        Assert.Single(results);
        Assert.Contains("Lightning-Bolt", results[0].Target.ProductUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static CardMarketScraper CreateScraper(
        IPlaywrightSessionFactory sessionFactory,
        ICardmarketSessionSetup setup,
        IScrapeDelayStrategy delayStrategy)
    {
        var appSettingsService = new StaticAppSettingsService(new AppSettingsSnapshot
        {
            Scraping = new ScrapingSettingsSnapshot
            {
                Headless = true,
                MaxConcurrentWorkers = 1,
                Languages = "1",
                MinCondition = 2,
                SellerCountry = "1",
                Proxies = []
            }
        });

        var proxyService = new PlaywrightProxyService(sessionFactory, appSettingsService);

        return new CardMarketScraper(sessionFactory, setup, proxyService, appSettingsService, delayStrategy);
    }

    private static ScrapingTarget Target(string productUrl)
    {
        return new ScrapingTarget
        {
            CardName = "Lightning Bolt",
            SetName = "Alpha",
            ProductUrl = productUrl,
            DesiredQuantity = 2
        };
    }

    private static async Task<List<MarketCardData>> ToListAsync(IAsyncEnumerable<MarketCardData> source)
    {
        var result = new List<MarketCardData>();

        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class StaticAppSettingsService : IAppSettingsService
    {
        private readonly AppSettingsSnapshot _snapshot;

        public StaticAppSettingsService(AppSettingsSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<AppSettingsSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_snapshot);

        public Task SaveAsync(AppSettingsSnapshot snapshot, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
