using System.Text.Json;
using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Infrastructure.Caching;
using CMBuyerStudio.Tests.Integration.Testing;

namespace CMBuyerStudio.Tests.Integration;

public sealed class MarketDataCacheServiceTests
{
    [Fact]
    public async Task GetAsync_ReturnsNullWhenEntryDoesNotExist()
    {
        using var paths = new TestAppPaths();
        var sut = new MarketDataCacheService(BuildSettingsService(ttlHours: 24), paths);

        var result = await sut.GetAsync(Target("https://example/missing"));

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_PersistsAndReturnsCachedEntry()
    {
        using var paths = new TestAppPaths();
        var sut = new MarketDataCacheService(BuildSettingsService(ttlHours: 24), paths);
        var marketData = MarketData("https://example/card-a", "Seller A", 1.10m);

        await sut.SaveAsync(marketData);
        var cached = await sut.GetAsync(Target("https://example/card-a"));

        Assert.NotNull(cached);
        Assert.Equal("Seller A", cached!.Offers.Single().SellerName);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingEntryByProductUrl()
    {
        using var paths = new TestAppPaths();
        var sut = new MarketDataCacheService(BuildSettingsService(ttlHours: 24), paths);

        await sut.SaveAsync(MarketData("https://example/card-a", "Seller A", 1.10m));
        await sut.SaveAsync(MarketData("https://example/card-a", "Seller B", 0.90m));

        var cached = await sut.GetAsync(Target("https://example/card-a"));

        Assert.NotNull(cached);
        Assert.Equal("Seller B", cached!.Offers.Single().SellerName);
        Assert.Equal(0.90m, cached.Offers.Single().Price);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForExpiredEntry()
    {
        using var paths = new TestAppPaths();
        var cacheFile = Path.Combine(paths.CardsCachePath, "market-data-cache.json");
        var staleEntry = new[]
        {
            new
            {
                ProductUrl = "https://example/stale",
                CachedAtUtc = DateTime.UtcNow.AddHours(-2),
                Data = MarketData("https://example/stale", "Seller A", 1.10m)
            }
        };

        await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(staleEntry));
        var sut = new MarketDataCacheService(BuildSettingsService(ttlHours: 1), paths);

        var cached = await sut.GetAsync(Target("https://example/stale"));

        Assert.Null(cached);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullWhenCacheFileIsCorrupt()
    {
        using var paths = new TestAppPaths();
        var cacheFile = Path.Combine(paths.CardsCachePath, "market-data-cache.json");
        await File.WriteAllTextAsync(cacheFile, "{bad-json");
        var sut = new MarketDataCacheService(BuildSettingsService(ttlHours: 24), paths);

        var cached = await sut.GetAsync(Target("https://example/card-a"));

        Assert.Null(cached);
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTemporaryFileBehind()
    {
        using var paths = new TestAppPaths();
        var sut = new MarketDataCacheService(BuildSettingsService(ttlHours: 24), paths);

        await sut.SaveAsync(MarketData("https://example/card-a", "Seller A", 1.10m));

        Assert.False(File.Exists(Path.Combine(paths.CardsCachePath, "market-data-cache.json.tmp")));
    }

    [Fact]
    public async Task SaveAsync_HandlesParallelReadsAndWrites()
    {
        using var paths = new TestAppPaths();
        var sut = new MarketDataCacheService(BuildSettingsService(ttlHours: 24), paths);
        var urls = Enumerable.Range(1, 4).Select(index => $"https://example/card-{index}").ToArray();

        var tasks = Enumerable.Range(0, 20).Select(async index =>
        {
            var url = urls[index % urls.Length];
            await sut.SaveAsync(MarketData(url, $"Seller {index}", 1 + index));
            return await sut.GetAsync(Target(url));
        });

        var results = await Task.WhenAll(tasks);

        foreach (var url in urls)
        {
            Assert.NotNull(await sut.GetAsync(Target(url)));
        }

        Assert.DoesNotContain(results, item => item is null);
        Assert.False(File.Exists(Path.Combine(paths.CardsCachePath, "market-data-cache.json.tmp")));
    }

    private static IAppSettingsService BuildSettingsService(int ttlHours)
        => new StaticAppSettingsService(new AppSettingsSnapshot
        {
            Cache = new CacheSettingsSnapshot
            {
                TtlHours = ttlHours
            }
        });

    private static ScrapingTarget Target(string productUrl)
    {
        return new ScrapingTarget
        {
            CardName = "Lightning Bolt",
            SetName = "Alpha",
            ProductUrl = productUrl,
            DesiredQuantity = 1
        };
    }

    private static MarketCardData MarketData(string productUrl, string sellerName, decimal price)
    {
        return new MarketCardData
        {
            Target = Target(productUrl),
            ScrapedAtUtc = DateTime.UtcNow,
            Offers =
            [
                new SellerOffer
                {
                    SellerName = sellerName,
                    Country = "Spain",
                    Price = price,
                    AvailableQuantity = 1,
                    CardName = "Lightning Bolt",
                    SetName = "Alpha",
                    ProductUrl = productUrl
                }
            ]
        };
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
