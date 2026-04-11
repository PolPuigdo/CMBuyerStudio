using CMBuyerStudio.Application.Enums;

namespace CMBuyerStudio.Application.Models;

public sealed class HtmlReportRequest
{
    public SellerScopeMode Scope { get; init; }

    public PurchaseOptimizationResult OptimizationResult { get; init; } = default!;

    public PurgedScopeSnapshot Snapshot { get; init; } = default!;

    public DateTimeOffset GeneratedAt { get; init; }
}
