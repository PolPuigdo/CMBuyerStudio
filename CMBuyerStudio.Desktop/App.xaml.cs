using System.Windows;
using CMBuyerStudio.Desktop.Composition;
using CMBuyerStudio.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CMBuyerStudio.Desktop;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddDesktop();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();

        base.OnExit(e);
    }
}