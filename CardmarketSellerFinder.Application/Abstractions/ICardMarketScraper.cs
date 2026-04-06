using CMBuyerStudio.Domain.Market;
using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Application.Abstractions
{
    public interface ICardMarketScraper
    {
        Task<MarketCardData> ScrapeAsync(ScrapingTarget target, CancellationToken cancellationToken = default);

        IAsyncEnumerable<MarketCardData> ScrapeManyAsync(IEnumerable<ScrapingTarget> targets, CancellationToken cancellationToken = default);
    }
}
