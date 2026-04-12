using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Infrastructure.Cardmarket.Builders;
using CMBuyerStudio.Infrastructure.Options;
using Microsoft.Playwright;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright;

public class PlaywrightProxyService
{
    private readonly IPlaywrightSessionFactory _playwrightSessionFactory;
    private readonly IAppSettingsService _appSettingsService;

    public PlaywrightProxyService(
        IPlaywrightSessionFactory playwrightSessionFactory,
        IAppSettingsService appSettingsService)
    {
        _playwrightSessionFactory = playwrightSessionFactory;
        _appSettingsService = appSettingsService;
    }

    private async Task<bool> IsProxyWorkingAsync(
        ProxyOptions proxyOptions,
        string url,
        bool headless = false,
        CancellationToken cancellationToken = default)
    {
        var proxy = ToPlaywrightProxy(proxyOptions);

        await using var session = await _playwrightSessionFactory.CreateChromiumAsync(headless, proxy);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await session.Page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 8000
            });

            await session.Page.WaitForTimeoutAsync(5000);

            if (response is null || !response.Ok)
            {
                return false;
            }

            var securityCheckLocator = session.Page.Locator("h2:has-text('Performing security verification'), h2:has-text('Verificación de seguridad en curso')");

            if (await securityCheckLocator.CountAsync() > 0)
            {
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static Proxy ToPlaywrightProxy(ProxyOptions proxyOptions)
    {
        return new Proxy
        {
            Server = proxyOptions.Server,
            Username = string.IsNullOrWhiteSpace(proxyOptions.Username) ? null : proxyOptions.Username,
            Password = string.IsNullOrWhiteSpace(proxyOptions.Password) ? null : proxyOptions.Password
        };
    }

    public async Task<IReadOnlyList<Proxy>> GetWorkingProxiesAsync(CancellationToken cancellationToken)
    {
        var settings = await _appSettingsService.GetCurrentAsync(cancellationToken);
        if (settings.Scraping.Proxies is not { Count: > 0 })
        {
            return [];
        }

        var proxyOptions = settings.Scraping.Proxies
            .Where(proxy => !string.IsNullOrWhiteSpace(proxy.Server))
            .Select(proxy => new ProxyOptions
            {
                Server = proxy.Server,
                Username = proxy.Username,
                Password = proxy.Password
            })
            .ToList();

        if (proxyOptions.Count == 0)
        {
            return [];
        }

        var semaphore = new SemaphoreSlim(6);
        var tasks = proxyOptions.Select(async proxy =>
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                var urlProxyChecker = string.IsNullOrWhiteSpace(settings.Scraping.UrlProxyChecker)
                    ? "https://www.cardmarket.com/es/Magic/Products/Singles/Innistrad-Remastered-Extras/Edgar-Markov-V1"
                    : settings.Scraping.UrlProxyChecker;

                var isWorking = await IsProxyWorkingAsync(
                    proxy,
                    urlProxyChecker,
                    headless: settings.Scraping.Headless,
                    cancellationToken);

                return isWorking
                    ? ToPlaywrightProxy(proxy)
                    : null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        return results
            .Where(proxy => proxy is not null)
            .Select(proxy => proxy!)
            .ToList();
    }
}
