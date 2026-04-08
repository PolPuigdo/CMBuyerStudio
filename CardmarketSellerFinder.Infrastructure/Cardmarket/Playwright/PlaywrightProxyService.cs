using CMBuyerStudio.Infrastructure.Cardmarket.Builders;
using CMBuyerStudio.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright
{
    public class PlaywrightProxyService
    {
        private readonly PlaywrightBuilder _playwrightBuilder;
        private readonly ScrapingOptions _scrapingOptions;

        public PlaywrightProxyService(PlaywrightBuilder playwrightBuilder, IOptions<ScrapingOptions> scrapingOptions)
        {
            _playwrightBuilder = playwrightBuilder;
            _scrapingOptions = scrapingOptions.Value;
        }

        private async Task<bool> IsProxyWorkingAsync(
            ProxyOptions proxyOp,
            string url,
            bool headless = false,
            CancellationToken cancellationToken = default)
        {
            var proxy = ToPlaywrightProxy(proxyOp);

            await using var session = await _playwrightBuilder.CreateChromiumAsync(headless, proxy);

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
            if (_scrapingOptions.Proxies is not { Count: > 0 })
            {
                return [];
            }

            var semaphore = new SemaphoreSlim(6);
            var tasks = _scrapingOptions.Proxies.Select(async proxyOptions =>
            {
                await semaphore.WaitAsync(cancellationToken);

                try
                {
                    var isWorking = await IsProxyWorkingAsync(
                        proxyOptions,
                        _scrapingOptions.UrlProxyCecker,
                        _scrapingOptions.Headless,
                        cancellationToken);

                    return isWorking
                        ? ToPlaywrightProxy(proxyOptions)
                        : null;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);

            return results
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();
        }
    }
}