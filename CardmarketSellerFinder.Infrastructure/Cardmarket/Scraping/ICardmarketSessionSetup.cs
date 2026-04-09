using Microsoft.Playwright;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping
{
    public interface ICardmarketSessionSetup
    {
        Task PrepareAsync(IPage page, string url, CancellationToken cancellationToken = default);
    }
}
