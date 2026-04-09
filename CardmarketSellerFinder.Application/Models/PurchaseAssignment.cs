namespace CMBuyerStudio.Application.Models;

public sealed record PurchaseAssignment(
    string SellerName,
    string ProductUrl,
    string CardName,
    string SetName,
    int Quantity,
    decimal UnitPrice,
    decimal TotalPrice);
