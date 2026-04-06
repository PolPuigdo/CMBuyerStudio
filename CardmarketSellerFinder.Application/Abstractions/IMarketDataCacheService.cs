using CMBuyerStudio.Domain.Market;
using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Application.Abstractions
{
    public interface IMarketDataCacheService
    {
        Task<MarketCardData?> GetAsync(
            ScrapingTarget target,
            CancellationToken cancellationToken = default);

        Task SaveAsync(
            MarketCardData marketData,
            CancellationToken cancellationToken = default);
    }
}
