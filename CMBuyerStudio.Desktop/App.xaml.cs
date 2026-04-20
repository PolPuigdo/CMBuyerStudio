using CMBuyerStudio.Desktop.Composition;
using CMBuyerStudio.Desktop.ErrorHandling;
using CMBuyerStudio.Desktop.ViewModels;
using CMBuyerStudio.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CMBuyerStudio.Desktop;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;
    private readonly IExceptionHandlingService _exceptionHandlingService;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddPersistence();
                services.AddApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                services.AddReporting();
                services.AddDesktop();
            })
            .Build();

        _exceptionHandlingService = _host.Services.GetRequiredService<IExceptionHandlingService>();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            await _host.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            var wantedCardsViewModel = _host.Services.GetRequiredService<WantedCardsViewModel>();

            await wantedCardsViewModel.InitializeAsync();

            mainWindow.Show();

            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            _exceptionHandlingService.Handle(exception, "App.OnStartup", isFatal: true);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;

        try
        {
            await _host.StopAsync();
        }
        catch (Exception exception)
        {
            _exceptionHandlingService.Handle(exception, "App.OnExit");
        }
        finally
        {
            _host.Dispose();
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _exceptionHandlingService.Handle(e.Exception, "DispatcherUnhandledException", isFatal: true);
        e.Handled = true;
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _exceptionHandlingService.Handle(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new Exception($"Unhandled exception object: {e.ExceptionObject}");

        _exceptionHandlingService.Handle(
            exception,
            "AppDomain.CurrentDomain.UnhandledException",
            isFatal: true);
    }
}
