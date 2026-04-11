namespace CMBuyerStudio.Domain.Market;

public sealed class ScrapingTarget
{
    public string RequestKey { get; init; } = string.Empty;
    public string CardName { get; init; } = default!;
    public string SetName { get; init; } = default!;
    public string ProductUrl { get; init; } = default!;
    public int DesiredQuantity { get; init; }

    public string CacheKey => ProductUrl.ToLowerInvariant();
}
