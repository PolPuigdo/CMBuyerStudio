namespace CMBuyerStudio.Application.Models;

public sealed class PurchaseOptimizationResult
{
    public IReadOnlyList<string> SelectedSellerNames { get; init; } = [];

    public IReadOnlyList<PurchaseAssignment> Assignments { get; init; } = [];

    public int SellerCount { get; init; }

    public decimal CardsTotalPrice { get; init; }

    public IReadOnlySet<string> UncoveredCardKeys { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
