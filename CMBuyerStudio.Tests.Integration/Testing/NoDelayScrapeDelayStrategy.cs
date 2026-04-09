using CMBuyerStudio.Infrastructure.Cardmarket.Scraping;

namespace CMBuyerStudio.Tests.Integration.Testing;

public sealed class NoDelayScrapeDelayStrategy : IScrapeDelayStrategy
{
    public Task DelayAfterScrapeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
