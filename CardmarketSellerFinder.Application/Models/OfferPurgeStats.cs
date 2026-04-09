namespace CMBuyerStudio.Application.Models;

public sealed class OfferPurgeStats
{
    public int InitialSellerCount { get; init; }
    public int RemainingSellerCount { get; init; }

    public int RemovedUseless { get; init; }
    public int RemovedSingleCardDominated { get; init; }
    public int RemovedGlobalDominated { get; init; }
}