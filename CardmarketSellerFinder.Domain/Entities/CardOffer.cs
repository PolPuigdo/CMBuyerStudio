namespace CMBuyerStudio.Domain.Entities;

public sealed record CardOffer(
    string SellerName,
    string SellerProfileUrl,
    string CardName,
    string SourceUrl,
    double Price,
    int AvailableQuantity,
    string SellerCountry,
    string Language,
    string Condition
);