using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Common.Options;
using CMBuyerStudio.Application.Optimization;
using CMBuyerStudio.Application.Services;
using CMBuyerStudio.Desktop.ViewModels;
using CMBuyerStudio.Desktop.Views;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Infrastructure.Caching;
using CMBuyerStudio.Infrastructure.Cardmarket.Builders;
using CMBuyerStudio.Infrastructure.Cardmarket.Cache;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using CMBuyerStudio.Infrastructure.Cardmarket.Scraping;
using CMBuyerStudio.Infrastructure.Options;
using CMBuyerStudio.Infrastructure.Paths;
using CMBuyerStudio.Infrastructure.Settings;
using CMBuyerStudio.Persistence.WantedCards;
using CMBuyerStudio.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Configuration;
using System.Net.Http.Headers;

namespace CMBuyerStudio.Desktop.Composition;

public static class ServiceRegistration
{
    public static IServiceCollection AddDesktop(this IServiceCollection services)
    {
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<WantedCardsViewModel>();
        services.AddSingleton<RunBestSellerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        services.AddSingleton<WantedCardsView>();

        services.AddSingleton<MainWindow>();

        return services;
    }

    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ShippingCostsOptions>(configuration.GetSection(ShippingCostsOptions.SectionName));
        services.Configure<PurchaseOptimizerOptions>(configuration.GetSection(PurchaseOptimizerOptions.SectionName));

        services.AddSingleton<IWantedCardsService, WantedCardsService>();
        services.AddSingleton<OfferPurger>();
        services.AddSingleton<PurchaseOptimizer>();
        services.AddSingleton<IRunAnalysisService, RunAnalysisService>();

        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();

        services.Configure<ScrapingOptions>(configuration.GetSection(ScrapingOptions.SectionName));
        services.AddSingleton<IMarketDataCacheService, MarketDataCacheService>();

        services.AddSingleton<IPlaywrightSessionFactory, PlaywrightBuilder>();
        services.AddSingleton<PlaywrightParser>();
        services.AddSingleton<ICardmarketSessionSetup, CardmarketSessionSetup>();
        services.AddSingleton<IScrapeDelayStrategy, DefaultScrapeDelayStrategy>();
        services.AddTransient<PlaywrightProxyService>();
        services.AddSingleton<ICardMarketScraper, CardMarketScraper>();
        
        services.AddHttpClient<ICardImageCacheService, CardImageCacheService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(25);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CMBuyerStudio/1.0");

            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("image/avif"));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("image/webp"));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("image/*"));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("*/*", 0.8));

            client.DefaultRequestHeaders.Referrer = new Uri("https://www.cardmarket.com/");
        });

        services.AddSingleton<ICardSearchService, CardSearchService>();

        return services;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IWantedCardsRepository, JsonWantedCardsRepository>();
        return services;
    }

    public static IServiceCollection AddReporting(this IServiceCollection services)
    {
        services.AddSingleton<IHtmlReportGenerator, HtmlReportGenerator>();

        return services;
    }
}
