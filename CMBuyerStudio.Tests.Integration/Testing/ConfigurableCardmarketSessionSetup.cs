using System.Collections.Concurrent;
using CMBuyerStudio.Infrastructure.Cardmarket.Scraping;
using Microsoft.Playwright;

namespace CMBuyerStudio.Tests.Integration.Testing;

public sealed class ConfigurableCardmarketSessionSetup : ICardmarketSessionSetup
{
    private readonly Func<string, int, Exception?> _failureFactory;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurableCardmarketSessionSetup(Func<string, int, Exception?> failureFactory)
    {
        _failureFactory = failureFactory;
    }

    public async Task PrepareAsync(IPage page, string url, CancellationToken cancellationToken = default)
    {
        var attempt = _attempts.AddOrUpdate(url, 1, static (_, current) => current + 1);
        var exception = _failureFactory(url, attempt);

        if (exception is not null)
        {
            throw exception;
        }

        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 10000
        });
    }
}
