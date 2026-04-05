using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using Microsoft.Playwright;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Builders
{
    public class PlaywrightBuilder
    {
        public async Task<PlaywrightSession> CreateChromiumAsync(bool headless = false, Proxy? proxy = null)
        {
            var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Proxy = proxy
            });
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            return new PlaywrightSession(playwright, browser, context, page);
        }

        public async Task<PlaywrightSession> CreateFirefoxAsync(bool headless = false, Proxy? proxy = null)
        {
            var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Proxy = proxy
            });
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            return new PlaywrightSession(playwright, browser, context, page);
        }
    }
}
