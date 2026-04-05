using Microsoft.Playwright;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright
{
    public sealed class PlaywrightSession : IAsyncDisposable
    {
        public IPlaywright Playwright { get; }
        public IBrowser Browser { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; }

        public PlaywrightSession(
            IPlaywright playwright,
            IBrowser browser,
            IBrowserContext context,
            IPage page)
        {
            Playwright = playwright;
            Browser = browser;
            Context = context;
            Page = page;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Browser.DisposeAsync();
            Playwright.Dispose();
        }
    }
}
