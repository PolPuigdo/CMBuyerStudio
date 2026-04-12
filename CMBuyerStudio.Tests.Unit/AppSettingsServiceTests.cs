using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;

namespace CMBuyerStudio.Tests.Unit;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public async Task GetCurrentAsync_WhenNoUserFile_ReturnsBaseConfiguration()
    {
        using var paths = new TestAppPaths();
        var sut = new AppSettingsService(BuildConfiguration(), paths);

        var snapshot = await sut.GetCurrentAsync();

        Assert.Equal(24, snapshot.Cache.TtlHours);
        Assert.Equal(3.0, snapshot.ShippingCosts.Default);
        Assert.Equal(1.45, snapshot.ShippingCosts.Countries["Spain"]);
        Assert.Equal("base-user", snapshot.Scraping.CardmarketUsername);
        Assert.Equal("base-password", snapshot.Scraping.CardmarketPassword);
        Assert.Equal("1", snapshot.Scraping.SellerCountry);
        Assert.Equal("1", snapshot.Scraping.Languages);
        Assert.Equal(2, snapshot.Scraping.MinCondition);
    }

    [Fact]
    public async Task SaveAsync_WritesEncryptedPasswordAndGetCurrentAsyncDecryptsIt()
    {
        using var paths = new TestAppPaths();
        var sut = new AppSettingsService(BuildConfiguration(), paths);

        await sut.SaveAsync(new AppSettingsSnapshot
        {
            Cache = new CacheSettingsSnapshot
            {
                TtlHours = 48
            },
            ShippingCosts = new ShippingCostsSettingsSnapshot
            {
                Default = 4.0,
                Countries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Spain"] = 2.0
                }
            },
            Scraping = new ScrapingSettingsSnapshot
            {
                Headless = false,
                MaxConcurrentWorkers = 10,
                UrlProxyChecker = "https://example/check",
                CardmarketUsername = "new-user",
                CardmarketPassword = "super-secret",
                SellerCountry = "1,10",
                Languages = "1,4",
                MinCondition = 3,
                Proxies =
                [
                    new ProxySettingsSnapshot
                    {
                        Server = "http://127.0.0.1:8080",
                        Username = "proxy-user",
                        Password = "proxy-pass"
                    }
                ]
            }
        });

        var userSettingsPath = Path.Combine(Path.GetDirectoryName(paths.CardsPath)!, "settings.user.json");
        var userSettingsJson = await File.ReadAllTextAsync(userSettingsPath);
        Assert.Contains("cardmarketPasswordProtected", userSettingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cardmarketPassword\":\"super-secret\"", userSettingsJson, StringComparison.Ordinal);

        var reloaded = await sut.GetCurrentAsync();
        Assert.Equal(48, reloaded.Cache.TtlHours);
        Assert.Equal(4.0, reloaded.ShippingCosts.Default);
        Assert.Equal(2.0, reloaded.ShippingCosts.Countries["Spain"]);
        Assert.Equal("new-user", reloaded.Scraping.CardmarketUsername);
        Assert.Equal("super-secret", reloaded.Scraping.CardmarketPassword);
        Assert.Equal("1,10", reloaded.Scraping.SellerCountry);
        Assert.Equal("1,4", reloaded.Scraping.Languages);
        Assert.Equal(3, reloaded.Scraping.MinCondition);
        Assert.Single(reloaded.Scraping.Proxies);
        Assert.Equal("http://127.0.0.1:8080", reloaded.Scraping.Proxies[0].Server);
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:TtlHours"] = "24",
                ["ShippingCosts:Default"] = "3.0",
                ["ShippingCosts:Countries:Spain"] = "1.45",
                ["ShippingCosts:Countries:Germany"] = "2.55",
                ["Scraping:Headless"] = "false",
                ["Scraping:MaxConcurrentWorkers"] = "10",
                ["Scraping:CardmarketUsername"] = "base-user",
                ["Scraping:CardmarketPassword"] = "base-password",
                ["Scraping:SellerCountry"] = "1",
                ["Scraping:UrlProxyCecker"] = "https://base/check",
                ["Scraping:Languages"] = "1",
                ["Scraping:MinCondition"] = "2",
                ["Scraping:Proxies:0:Server"] = "http://10.0.0.1:9000",
                ["Scraping:Proxies:0:Username"] = "base-proxy-user",
                ["Scraping:Proxies:0:Password"] = "base-proxy-pass"
            })
            .Build();
    }

    private sealed class TestAppPaths : IAppPaths, IDisposable
    {
        private readonly string _rootPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSettingsServiceTests-{Guid.NewGuid():N}");

        public TestAppPaths()
        {
            Directory.CreateDirectory(_rootPath);
            Directory.CreateDirectory(CachePath);
            Directory.CreateDirectory(ReportsPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CardsCachePath);
            Directory.CreateDirectory(ImageCardsPath);
        }

        public string CardsPath => Path.Combine(_rootPath, "cards.json");
        public string CachePath => Path.Combine(_rootPath, "Cache");
        public string ReportsPath => Path.Combine(_rootPath, "Reports");
        public string LogsPath => Path.Combine(_rootPath, "Logs");
        public string CardsCachePath => Path.Combine(CachePath, "CardsCache");
        public string ImageCardsPath => Path.Combine(CachePath, "Images");

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }
}
