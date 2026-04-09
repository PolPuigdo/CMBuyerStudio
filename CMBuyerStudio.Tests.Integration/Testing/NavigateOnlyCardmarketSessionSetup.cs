using CMBuyerStudio.Infrastructure.Cardmarket.Scraping;
using Microsoft.Playwright;

namespace CMBuyerStudio.Tests.Integration.Testing;

public sealed class NavigateOnlyCardmarketSessionSetup : ICardmarketSessionSetup
{
    public async Task PrepareAsync(IPage page, string url, CancellationToken cancellationToken = default)
    {
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 10000
        });
    }
}
