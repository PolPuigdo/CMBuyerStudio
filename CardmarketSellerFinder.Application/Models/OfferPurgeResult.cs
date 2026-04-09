using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;

namespace CMBuyerStudio.Application.Models;

public sealed class OfferPurgeResult
{
    public IReadOnlyList<MarketCardData> PurgedMarketData { get; init; } = [];

    public OfferPurgeStats Stats { get; init; } = new();

    public IReadOnlySet<string> PreselectedSellerNames { get; init; }
    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> UncoveredCardKeys { get; init; }
    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}