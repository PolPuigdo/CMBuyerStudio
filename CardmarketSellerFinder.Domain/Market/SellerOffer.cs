namespace CMBuyerStudio.Domain.Market;

public sealed class SellerOffer
{
    public string SellerName { get; init; } = default!;
    public string Country { get; init; } = default!;

    public decimal Price { get; init; }
    public int AvailableQuantity { get; init; }

    public string CardName { get; init; } = default!;
    public string SetName { get; init; } = default!;

    public string ProductUrl { get; init; } = default!;
}