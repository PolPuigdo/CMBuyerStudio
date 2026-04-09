using Microsoft.Playwright;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright
{
    public interface IPlaywrightSessionFactory
    {
        Task<PlaywrightSession> CreateChromiumAsync(bool headless = false, Proxy? proxy = null);
        Task<PlaywrightSession> CreateFirefoxAsync(bool headless = false, Proxy? proxy = null);
    }
}
