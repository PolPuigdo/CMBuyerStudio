using CMBuyerStudio.Application.RunAnalysis;
using CMBuyerStudio.Domain.Market;

namespace CMBuyerStudio.Application.Abstractions
{
    public interface ICardMarketScraper
    {
        Task<MarketCardData> ScrapeAsync(ScrapingTarget target, CancellationToken cancellationToken = default);

        IAsyncEnumerable<MarketCardData> ScrapeManyAsync(IEnumerable<ScrapingTarget> targets, IProgress<RunProgressEvent> progress, CancellationToken cancellationToken = default);
    }
}
