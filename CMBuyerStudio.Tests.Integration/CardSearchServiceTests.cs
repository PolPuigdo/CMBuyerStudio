using System.Net;
using CMBuyerStudio.Infrastructure.Cardmarket.Cache;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using CMBuyerStudio.Infrastructure.Cardmarket.Scraping;
using CMBuyerStudio.Tests.Integration.Testing;

namespace CMBuyerStudio.Tests.Integration;

public sealed class CardSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_ParsesDeduplicatesSortsAndDownloadsImages()
    {
        using var paths = new TestAppPaths();
        var fixtureHtml = FixtureLoader.LoadText("search-results.html");
        var sessionFactory = new TestPlaywrightSessionFactory(_ => TestRouteResponse.Html(fixtureHtml));
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            FakeHttpMessageHandler.CreateResponse(HttpStatusCode.OK, [1, 2, 3], request.RequestUri!.AbsoluteUri.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/png")));
        using var httpClient = new HttpClient(handler);
        var imageCache = new CardImageCacheService(httpClient, paths);
        var sut = new CardSearchService(sessionFactory, new PlaywrightParser(), imageCache);

        var results = await sut.SearchAsync("Lightning Bolt");

        Assert.Equal(2, results.Count);
        Assert.Equal("Magic 2011", results[0].SetName);
        Assert.Equal(0.80m, results[0].Price);
        Assert.Equal("Alpha", results[1].SetName);
        Assert.All(results, result => Assert.True(File.Exists(result.ImagePath)));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SearchAsync_ThrowsWhenCancelled()
    {
        var fixtureHtml = FixtureLoader.LoadText("search-results.html");
        var sessionFactory = new TestPlaywrightSessionFactory(_ => TestRouteResponse.Html(fixtureHtml));
        var imageCache = new StubCardImageCacheService();
        var sut = new CardSearchService(sessionFactory, new PlaywrightParser(), imageCache);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.SearchAsync("Lightning Bolt", cts.Token));
    }

    private sealed class StubCardImageCacheService : CMBuyerStudio.Application.Abstractions.ICardImageCacheService
    {
        public Task<string> GetOrDownloadAsync(string imageUrl, string imageName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }
    }
}
