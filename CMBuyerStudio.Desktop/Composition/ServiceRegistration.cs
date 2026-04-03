using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Desktop.ViewModels;
using CMBuyerStudio.Desktop.Views;
using CMBuyerStudio.Infrastructure.Paths;
using CMBuyerStudio.Persistence.WantedCards;
using Microsoft.Extensions.DependencyInjection;

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

    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {

        return services;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IWantedCardsRepository, JsonWantedCardsRepository>();
        return services;
    }

    public static IServiceCollection AddReporting(this IServiceCollection services)
    {

        return services;
    }
}