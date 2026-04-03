using CMBuyerStudio.Desktop.ViewModels;
using CMBuyerStudio.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CMBuyerStudio.Desktop.Composition;

public static class ServiceRegistration
{
    public static IServiceCollection AddDesktop(this IServiceCollection services)
    {
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}