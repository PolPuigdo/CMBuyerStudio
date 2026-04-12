using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Desktop.ViewModels;
using CMBuyerStudio.Tests.Desktop.Testing;

namespace CMBuyerStudio.Tests.Desktop;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Constructor_LoadsSnapshotIntoEditableState()
    {
        var service = new RecordingAppSettingsService(BuildSnapshot());
        var sut = new SettingsViewModel(service);

        await AsyncTestHelper.WaitUntilAsync(() => !sut.IsLoading);

        Assert.Equal(24, sut.CacheTtlHours);
        Assert.Equal(3.0, sut.ShippingDefaultCost);
        Assert.Equal("cm-user", sut.CardmarketUsername);
        Assert.Equal("cm-password", sut.CardmarketPassword);
        Assert.Equal(2, sut.SelectedMinConditionId);
        Assert.True(sut.SellerCountryOptions.Single(option => option.Id == 1).IsSelected);
        Assert.True(sut.SellerCountryOptions.Single(option => option.Id == 10).IsSelected);
        Assert.True(sut.LanguageOptions.Single(option => option.Id == 1).IsSelected);
        Assert.False(sut.IsDirty);

        var spainShipping = sut.ShippingCosts.Single(item => item.Country == "Spain");
        Assert.Equal(1.45, spainShipping.Cost);
    }

    [Fact]
    public async Task SaveCommand_PersistsSnapshotWithCatalogOrderedCsvAndResetsDirtyFlag()
    {
        var service = new RecordingAppSettingsService(BuildSnapshot());
        var sut = new SettingsViewModel(service);

        await AsyncTestHelper.WaitUntilAsync(() => !sut.IsLoading);

        sut.CacheTtlHours = 72;
        sut.SellerCountryOptions.Single(option => option.Id == 1).IsSelected = false;
        sut.SellerCountryOptions.Single(option => option.Id == 10).IsSelected = true;
        sut.SellerCountryOptions.Single(option => option.Id == 2).IsSelected = true;

        sut.LanguageOptions.Single(option => option.Id == 1).IsSelected = false;
        sut.LanguageOptions.Single(option => option.Id == 4).IsSelected = true;
        sut.LanguageOptions.Single(option => option.Id == 3).IsSelected = true;

        sut.SelectedMinConditionId = 4;
        sut.AddProxyCommand.Execute(null);
        var addedProxy = sut.Proxies.Last();
        addedProxy.Server = "http://127.0.0.1:8080";
        addedProxy.Username = "proxy-user";
        addedProxy.Password = "proxy-pass";

        Assert.True(sut.IsDirty);

        sut.SaveCommand.Execute(null);
        await AsyncTestHelper.WaitUntilAsync(() => !sut.IsSaving && service.SaveCallCount == 1);

        var saved = service.LastSavedSnapshot!;
        Assert.Equal(72, saved.Cache.TtlHours);
        Assert.Equal("2,10", saved.Scraping.SellerCountry);
        Assert.Equal("3,4", saved.Scraping.Languages);
        Assert.Equal(4, saved.Scraping.MinCondition);
        Assert.Contains(saved.Scraping.Proxies, proxy => proxy.Server == "http://127.0.0.1:8080");
        Assert.False(sut.IsDirty);
    }

    private static AppSettingsSnapshot BuildSnapshot()
    {
        return new AppSettingsSnapshot
        {
            Cache = new CacheSettingsSnapshot
            {
                TtlHours = 24
            },
            ShippingCosts = new ShippingCostsSettingsSnapshot
            {
                Default = 3.0,
                Countries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Spain"] = 1.45
                }
            },
            Scraping = new ScrapingSettingsSnapshot
            {
                Headless = false,
                MaxConcurrentWorkers = 10,
                UrlProxyChecker = "https://example/check",
                CardmarketUsername = "cm-user",
                CardmarketPassword = "cm-password",
                SellerCountry = "1,10",
                Languages = "1",
                MinCondition = 2,
                Proxies = []
            }
        };
    }

    private sealed class RecordingAppSettingsService : IAppSettingsService
    {
        private readonly AppSettingsSnapshot _snapshot;

        public RecordingAppSettingsService(AppSettingsSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int SaveCallCount { get; private set; }
        public AppSettingsSnapshot? LastSavedSnapshot { get; private set; }

        public Task<AppSettingsSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_snapshot);

        public Task SaveAsync(AppSettingsSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            LastSavedSnapshot = snapshot;
            return Task.CompletedTask;
        }
    }
}
