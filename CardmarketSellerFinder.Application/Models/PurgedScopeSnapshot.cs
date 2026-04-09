using CMBuyerStudio.Domain.Market;

namespace CMBuyerStudio.Application.Models;

public sealed class PurgedScopeSnapshot
{
    public IReadOnlyList<MarketCardData> MarketData { get; init; } = [];

    public IReadOnlyDictionary<string, decimal> FixedCostBySellerName { get; init; }
        = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> PreselectedSellerNames { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> UncoveredCardKeys { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}