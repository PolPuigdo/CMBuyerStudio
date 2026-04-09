using CMBuyerStudio.Domain.Market;

namespace CMBuyerStudio.Application.Models;

public sealed class PurgedScopeSnapshot
{
    public IReadOnlyList<MarketCardData> ScopedMarketData { get; init; } = [];

    public IReadOnlyList<MarketCardData> PurgedMarketData { get; init; } = [];

    public IReadOnlyDictionary<string, int> RemainingRequiredByCardKey { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, decimal> FixedCostBySellerName { get; init; }
        = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> PreselectedSellerNames { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> UncoveredCardKeys { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
