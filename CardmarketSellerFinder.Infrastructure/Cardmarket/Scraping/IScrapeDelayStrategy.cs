namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping
{
    public interface IScrapeDelayStrategy
    {
        Task DelayAfterScrapeAsync(CancellationToken cancellationToken = default);
    }
}
