using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping
{
    public sealed class DefaultScrapeDelayStrategy : IScrapeDelayStrategy
    {
        public Task DelayAfterScrapeAsync(CancellationToken cancellationToken = default)
        {
            return Task.Delay(WaitTiming.GetRandom(25000, 45000), cancellationToken);
        }
    }
}
