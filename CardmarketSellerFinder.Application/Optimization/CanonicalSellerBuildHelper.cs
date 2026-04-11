using CMBuyerStudio.Domain.Market;

namespace CMBuyerStudio.Application.Optimization;

internal sealed class SellerCardProfileData
{
    public SellerCardProfileData(decimal[] prefixCosts)
    {
        PrefixCosts = prefixCosts;
    }

    public decimal[] PrefixCosts { get; }

    public int QtyUsable => PrefixCosts.Length - 1;
}

internal sealed class SellerAccumulatorData
{
    private readonly List<SellerOffer>?[] _offersByCard;

    public SellerAccumulatorData(int originalOrder, string sellerName, int cardCount)
    {
        OriginalOrder = originalOrder;
        SellerName = sellerName;
        _offersByCard = new List<SellerOffer>?[cardCount];
    }

    public int OriginalOrder { get; }

    public string SellerName { get; }

    public void AddOffer(int cardIndex, SellerOffer offer)
    {
        _offersByCard[cardIndex] ??= [];
        _offersByCard[cardIndex]!.Add(offer);
    }

    public IReadOnlyList<SellerOffer>? GetOffers(int cardIndex)
        => _offersByCard[cardIndex];
}

internal sealed record CanonicalSellerBuildMetrics(
    int CardCount,
    int OfferCount,
    int UniqueSellerCount,
    int ActiveSellerCount,
    int ActiveProfileCount);

internal sealed record CanonicalSellerBuildResult<TSeller>(
    IReadOnlyList<TSeller> Sellers,
    CanonicalSellerBuildMetrics Metrics)
    where TSeller : class;

internal static class CanonicalSellerBuildHelper
{
    public static CanonicalSellerBuildResult<TSeller> BuildCanonicalSellers<TSeller>(
        IReadOnlyList<MarketCardData> marketData,
        int[] requiredByCard,
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName,
        Func<int, string, decimal, SellerCardProfileData?[], int[], int[], TSeller> createSeller,
        int maxDegreeOfParallelism,
        Func<string, IReadOnlyDictionary<string, decimal>, decimal>? resolveFixedCost = null)
        where TSeller : class
    {
        ArgumentNullException.ThrowIfNull(marketData);
        ArgumentNullException.ThrowIfNull(requiredByCard);
        ArgumentNullException.ThrowIfNull(fixedCostBySellerName);
        ArgumentNullException.ThrowIfNull(createSeller);

        var sellerIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var accumulators = new List<SellerAccumulatorData>();
        var cardCount = marketData.Count;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            foreach (var offer in marketData[cardIndex].Offers)
            {
                if (!sellerIndexByName.TryGetValue(offer.SellerName, out var sellerIndex))
                {
                    sellerIndex = accumulators.Count;
                    sellerIndexByName[offer.SellerName] = sellerIndex;
                    accumulators.Add(new SellerAccumulatorData(
                        sellerIndex,
                        offer.SellerName,
                        cardCount));
                }

                accumulators[sellerIndex].AddOffer(cardIndex, offer);
            }
        }

        var builtSellers = new TSeller?[accumulators.Count];
        var activeProfileCounts = new int[accumulators.Count];
        var effectiveParallelism = Math.Max(1, maxDegreeOfParallelism);

        if (effectiveParallelism <= 1 || accumulators.Count <= 1)
        {
            for (var sellerIndex = 0; sellerIndex < accumulators.Count; sellerIndex++)
            {
                var sellerBuild = BuildSeller(
                    accumulators[sellerIndex],
                    cardCount,
                    requiredByCard,
                    fixedCostBySellerName,
                    createSeller,
                    resolveFixedCost);
                builtSellers[sellerIndex] = sellerBuild.Seller;
                activeProfileCounts[sellerIndex] = sellerBuild.ActiveProfileCount;
            }
        }
        else
        {
            Parallel.For(
                0,
                accumulators.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = effectiveParallelism
                },
                sellerIndex =>
                {
                    var sellerBuild = BuildSeller(
                        accumulators[sellerIndex],
                        cardCount,
                        requiredByCard,
                        fixedCostBySellerName,
                        createSeller,
                        resolveFixedCost);
                    builtSellers[sellerIndex] = sellerBuild.Seller;
                    activeProfileCounts[sellerIndex] = sellerBuild.ActiveProfileCount;
                });
        }

        var sellers = builtSellers
            .Where(seller => seller is not null)
            .Select(seller => seller!)
            .ToList();

        var metrics = new CanonicalSellerBuildMetrics(
            CardCount: cardCount,
            OfferCount: marketData.Sum(card => card.Offers.Count),
            UniqueSellerCount: accumulators.Count,
            ActiveSellerCount: sellers.Count,
            ActiveProfileCount: activeProfileCounts.Sum());

        return new CanonicalSellerBuildResult<TSeller>(sellers, metrics);
    }

    public static SellerCardProfileData? BuildSellerCardProfile(
        IReadOnlyList<SellerOffer>? offers,
        int requiredQuantity)
    {
        if (requiredQuantity <= 0 || offers is null || offers.Count == 0)
        {
            return null;
        }

        var orderedOffers = offers
            .Where(offer => offer.AvailableQuantity > 0)
            .OrderBy(offer => offer.Price)
            .ThenByDescending(offer => offer.AvailableQuantity)
            .ThenBy(offer => offer.ProductUrl, StringComparer.OrdinalIgnoreCase)
            .ThenBy(offer => offer.CardName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(offer => offer.SetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orderedOffers.Count == 0)
        {
            return null;
        }

        var totalStock = orderedOffers.Sum(offer => offer.AvailableQuantity);
        var qtyUsable = Math.Min(requiredQuantity, totalStock);

        if (qtyUsable <= 0)
        {
            return null;
        }

        var prefixCosts = new decimal[qtyUsable + 1];
        var covered = 0;

        foreach (var offer in orderedOffers)
        {
            if (covered >= qtyUsable)
            {
                break;
            }

            var take = Math.Min(offer.AvailableQuantity, qtyUsable - covered);
            for (var unit = 0; unit < take; unit++)
            {
                covered++;
                prefixCosts[covered] = prefixCosts[covered - 1] + offer.Price;
            }
        }

        return covered == qtyUsable
            ? new SellerCardProfileData(prefixCosts)
            : null;
    }

    private static (TSeller Seller, int ActiveProfileCount) BuildSeller<TSeller>(
        SellerAccumulatorData accumulator,
        int cardCount,
        int[] requiredByCard,
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName,
        Func<int, string, decimal, SellerCardProfileData?[], int[], int[], TSeller> createSeller,
        Func<string, IReadOnlyDictionary<string, decimal>, decimal>? resolveFixedCost)
        where TSeller : class
    {
        var profiles = new SellerCardProfileData?[cardCount];
        var qtyByCard = new int[cardCount];
        var activeCards = new List<int>(cardCount);
        var activeProfileCount = 0;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            var profile = BuildSellerCardProfile(
                accumulator.GetOffers(cardIndex),
                requiredByCard[cardIndex]);

            if (profile is null || profile.QtyUsable <= 0)
            {
                continue;
            }

            profiles[cardIndex] = profile;
            qtyByCard[cardIndex] = profile.QtyUsable;
            activeCards.Add(cardIndex);
            activeProfileCount++;
        }

        return (
            createSeller(
                accumulator.OriginalOrder,
                accumulator.SellerName,
                resolveFixedCost is null
                    ? ResolveSellerFixedCost(accumulator.SellerName, fixedCostBySellerName)
                    : resolveFixedCost(accumulator.SellerName, fixedCostBySellerName),
                profiles,
                qtyByCard,
                [.. activeCards]),
            activeProfileCount);
    }

    private static decimal ResolveSellerFixedCost(
        string sellerName,
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName)
    {
        if (!fixedCostBySellerName.TryGetValue(sellerName, out var value) || value < 0m)
        {
            return 0m;
        }

        return value;
    }
}
