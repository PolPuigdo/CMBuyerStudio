using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Application.Services;
using CMBuyerStudio.Domain.Market;

namespace CMBuyerStudio.Tests.Unit;

public sealed class OfferPurgerTests
{
    private static readonly IReadOnlyDictionary<string, decimal> NoFixedCosts =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Purge_ExcludesInitiallyImpossibleCardsBeforeRunningTheMainRounds()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 3,
                Offer("SharedExpensive", "p1", price: 1m, availableQuantity: 1),
                Offer("OnlyImpossible", "p1", price: 2m, availableQuantity: 1)),
            Card(
                "p2",
                desiredQuantity: 1,
                Offer("SharedExpensive", "p2", price: 10m, availableQuantity: 1),
                Offer("CheapSingle", "p2", price: 5m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Equal(["p1"], result.UncoveredCardKeys.OrderBy(x => x));
        Assert.Equal(["CheapSingle"], result.PreselectedSellerNames.OrderBy(x => x));
        Assert.Empty(GetRemainingSellerNames(result));
    }

    [Fact]
    public void Purge_RebuildsWithRemainingRequiredAfterForcedSellers()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 1,
                Offer("ForcedBridge", "p1", price: 1m, availableQuantity: 1)),
            Card(
                "p2",
                desiredQuantity: 1,
                Offer("ForcedBridge", "p2", price: 1m, availableQuantity: 1),
                Offer("BridgeExpensive", "p2", price: 10m, availableQuantity: 1)),
            Card(
                "p3",
                desiredQuantity: 1,
                Offer("BridgeExpensive", "p3", price: 10m, availableQuantity: 1),
                Offer("FinalCheap", "p3", price: 5m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Equal(
            ["FinalCheap", "ForcedBridge"],
            result.PreselectedSellerNames.OrderBy(x => x));
        Assert.Empty(result.UncoveredCardKeys);
        Assert.Empty(GetRemainingSellerNames(result));
        Assert.DoesNotContain("BridgeExpensive", result.PreselectedSellerNames);
    }

    [Fact]
    public void Purge_RebuildsWithRemainingRequiredAfterIsolatedSolutions()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 1,
                Offer("IsolatedBest", "p1", price: 1m, availableQuantity: 1),
                Offer("IsolatedWorse", "p1", price: 2m, availableQuantity: 1)),
            Card(
                "p2",
                desiredQuantity: 1,
                Offer("OtherSeller", "p2", price: 7m, availableQuantity: 1),
                Offer("OtherBetter", "p2", price: 3m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Equal(
            ["IsolatedBest", "OtherBetter"],
            result.PreselectedSellerNames.OrderBy(x => x));
        Assert.Empty(result.UncoveredCardKeys);
        Assert.Empty(GetRemainingSellerNames(result));
        Assert.DoesNotContain("IsolatedWorse", result.PreselectedSellerNames);
    }

    [Fact]
    public void Purge_StillRemovesGloballyDominatedSellers()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 1,
                Offer("BetterAll", "p1", price: 1m, availableQuantity: 1),
                Offer("WorseAll", "p1", price: 2m, availableQuantity: 1)),
            Card(
                "p2",
                desiredQuantity: 1,
                Offer("BetterAll", "p2", price: 1m, availableQuantity: 1),
                Offer("WorseAll", "p2", price: 2m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Equal(["BetterAll"], result.PreselectedSellerNames.OrderBy(x => x));
        Assert.Empty(GetRemainingSellerNames(result));
        Assert.Empty(result.UncoveredCardKeys);
    }

    [Fact]
    public void Purge_PrefersSellerWithLowerFixedCostWhenPricesTie()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 1,
                Offer("CheapShipping", "p1", price: 1m, availableQuantity: 1),
                Offer("ExpensiveShipping", "p1", price: 1m, availableQuantity: 1))
        };
        var fixedCosts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["CheapShipping"] = 1m,
            ["ExpensiveShipping"] = 5m
        };

        var result = sut.Purge(marketData, fixedCosts);

        Assert.Contains("CheapShipping", result.PreselectedSellerNames);
        Assert.DoesNotContain("ExpensiveShipping", result.PreselectedSellerNames);
    }

    [Fact]
    public void Purge_RequiresMultipleSellersWhenNoSingleSellerCanCoverRequestedQuantity()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 2,
                Offer("SellerA", "p1", price: 1m, availableQuantity: 1),
                Offer("SellerB", "p1", price: 1.1m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Equal(["SellerA", "SellerB"], result.PreselectedSellerNames.OrderBy(x => x));
        Assert.Empty(result.UncoveredCardKeys);
    }

    [Fact]
    public void Purge_LeavesCardUncoveredWhenTotalAvailableQuantityIsInsufficient()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 3,
                Offer("SellerA", "p1", price: 1m, availableQuantity: 1),
                Offer("SellerB", "p1", price: 1.1m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Contains("p1", result.UncoveredCardKeys);
    }

    [Fact]
    public void Purge_CollapsesSellerNamesCaseInsensitively()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 1,
                Offer("SellerA", "p1", price: 1m, availableQuantity: 1),
                Offer("sellera", "p1", price: 1m, availableQuantity: 1)),
            Card(
                "p2",
                desiredQuantity: 1,
                Offer("SellerA", "p2", price: 1m, availableQuantity: 1),
                Offer("sellera", "p2", price: 1m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);
        var distinctIgnoringCase = result.PreselectedSellerNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Single(distinctIgnoringCase);
        Assert.Empty(result.UncoveredCardKeys);
    }

    [Fact]
    public void Purge_UsesRequestKeyForRemainingRequiredAndUncoveredCards()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "https://example.com/variant-a",
                3,
                "Lightning Bolt",
                Offer("SellerA", "https://example.com/variant-a", price: 1m, availableQuantity: 1),
                Offer("SellerB", "https://example.com/variant-b", price: 1.1m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Contains("Lightning Bolt", result.UncoveredCardKeys);
        Assert.Equal(0, result.RemainingRequiredByCardKey["Lightning Bolt"]);
        Assert.DoesNotContain("https://example.com/variant-a", result.RemainingRequiredByCardKey.Keys);
    }

    [Fact]
    public void Purge_EmitsProfilePhasesForInstrumentation()
    {
        var sut = new OfferPurger();
        var marketData = new[]
        {
            Card(
                "p1",
                desiredQuantity: 1,
                Offer("SellerA", "p1", price: 1m, availableQuantity: 1),
                Offer("SellerB", "p1", price: 2m, availableQuantity: 1)),
            Card(
                "p2",
                desiredQuantity: 1,
                Offer("SellerA", "p2", price: 1m, availableQuantity: 1))
        };

        var result = sut.Purge(marketData, NoFixedCosts);

        Assert.Contains(result.ProfilePhases, phase => phase.Name == "Purge.BuildCanonicalSellers.Initial");
        Assert.Contains(result.ProfilePhases, phase => phase.Name == "Purge.Total");
        Assert.Contains(result.ProfilePhases, phase => phase.Counters.ContainsKey("remainingSellers"));
    }

    private static MarketCardData Card(string productUrl, int desiredQuantity, params SellerOffer[] offers)
        => Card(productUrl, desiredQuantity, requestKey: string.Empty, offers);

    private static MarketCardData Card(string productUrl, int desiredQuantity, string requestKey, params SellerOffer[] offers)
    {
        return new MarketCardData
        {
            Target = new ScrapingTarget
            {
                RequestKey = requestKey,
                CardName = productUrl,
                SetName = "SET",
                ProductUrl = productUrl,
                DesiredQuantity = desiredQuantity
            },
            ScrapedAtUtc = DateTime.UtcNow,
            Offers = offers
        };
    }

    private static SellerOffer Offer(string sellerName, string productUrl, decimal price, int availableQuantity)
    {
        return new SellerOffer
        {
            SellerName = sellerName,
            Country = "ES",
            Price = price,
            AvailableQuantity = availableQuantity,
            CardName = productUrl,
            SetName = "SET",
            ProductUrl = productUrl
        };
    }

    private static IReadOnlyList<string> GetRemainingSellerNames(OfferPurgeResult result)
    {
        return result.PurgedMarketData
            .SelectMany(card => card.Offers)
            .Select(offer => offer.SellerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
