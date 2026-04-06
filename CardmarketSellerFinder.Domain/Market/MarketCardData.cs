namespace CMBuyerStudio.Domain.Market;

public sealed class MarketCardData
{
    public ScrapingTarget Target { get; init; } = default!;

    public IReadOnlyList<SellerOffer> Offers { get; init; } = [];

    public DateTime ScrapedAtUtc { get; init; }
}