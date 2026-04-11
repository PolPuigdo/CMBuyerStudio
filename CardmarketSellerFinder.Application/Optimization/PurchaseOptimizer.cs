using CMBuyerStudio.Application.Common.Options;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

namespace CMBuyerStudio.Application.Optimization;

public sealed class PurchaseOptimizer
{
    private const decimal CostEpsilon = 0.000001m;
    private const decimal InfiniteCost = decimal.MaxValue / 4m;

    private readonly PurchaseOptimizerOptions _options;

    public PurchaseOptimizer(IOptions<PurchaseOptimizerOptions> options)
    {
        _options = options?.Value ?? new PurchaseOptimizerOptions();
    }

    public PurchaseOptimizationResult Optimize(PurgedScopeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var selectedSellerNames = new HashSet<string>(
            snapshot.PreselectedSellerNames,
            StringComparer.OrdinalIgnoreCase);
        var knownUncoveredCardKeys = new HashSet<string>(
            snapshot.UncoveredCardKeys,
            StringComparer.OrdinalIgnoreCase);

        if (snapshot.ScopedMarketData.Count == 0)
        {
            return BuildResult(snapshot.ScopedMarketData, selectedSellerNames, knownUncoveredCardKeys);
        }

        var requiredByCard = snapshot.PurgedMarketData
            .Select(card => ResolveRemainingRequiredQuantity(
                ResolveCardKey(card.Target),
                card.Target.DesiredQuantity,
                snapshot.RemainingRequiredByCardKey))
            .ToArray();

        if (requiredByCard.Length == 0 || requiredByCard.All(quantity => quantity <= 0))
        {
            return BuildResult(snapshot.ScopedMarketData, selectedSellerNames, knownUncoveredCardKeys);
        }

        var settings = ResolveRuntimeSettings(_options);
        var canonicalSellers = BuildCanonicalSellers(
            snapshot.PurgedMarketData,
            requiredByCard,
            snapshot.FixedCostBySellerName);

        if (canonicalSellers.Count == 0)
        {
            AddUncoveredCardKeys(snapshot.PurgedMarketData, requiredByCard, knownUncoveredCardKeys);
            return BuildResult(snapshot.ScopedMarketData, selectedSellerNames, knownUncoveredCardKeys);
        }

        var activeRequiredByCard = requiredByCard.ToArray();
        if (activeRequiredByCard.All(quantity => quantity <= 0))
        {
            return BuildResult(snapshot.ScopedMarketData, selectedSellerNames, knownUncoveredCardKeys);
        }

        var candidatePoolBuilder = new CandidatePoolBuilder();
        var candidateSellerNames = candidatePoolBuilder.BuildCandidateSellerNames(
            canonicalSellers,
            activeRequiredByCard,
            snapshot.PreselectedSellerNames,
            settings);

        var reducedSellers = canonicalSellers
            .Where(seller => candidateSellerNames.Contains(seller.SellerName))
            .ToList();

        if (reducedSellers.Count == 0)
        {
            AddUncoveredCardKeys(snapshot.PurgedMarketData, activeRequiredByCard, knownUncoveredCardKeys);
            return BuildResult(snapshot.ScopedMarketData, selectedSellerNames, knownUncoveredCardKeys);
        }

        var cardCount = activeRequiredByCard.Length;
        var orderedReducedSellers = OrderSellersForSearch(
            reducedSellers,
            activeRequiredByCard,
            cardCount).ToList();

        var beamResult = new BeamSearchSolver(
            orderedReducedSellers,
            activeRequiredByCard,
            settings)
            .Run();

        var reducedExactResult = new ReducedExactSolver(
            orderedReducedSellers,
            activeRequiredByCard,
            cardCount,
            ResolveParallelism(),
            settings)
            .Solve(beamResult);

        HashSet<string> activeSelection;
        if (reducedExactResult.IsFullCoverage || beamResult.IsFullCoverage)
        {
            activeSelection = reducedExactResult.SelectedOrderedSellerIndices
                .Select(index => orderedReducedSellers[index].SellerName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!reducedExactResult.IsFullCoverage && beamResult.IsFullCoverage)
            {
                activeSelection = beamResult.SelectedOrderedSellerIndices
                    .Select(index => orderedReducedSellers[index].SellerName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            if (settings.EnableFinalCostRefine
                && activeSelection.Count > 0
                && activeSelection.Count <= settings.ExactMaxK)
            {
                activeSelection = RefineSelectionForFixedSellerCountOnCanonical(
                    orderedReducedSellers,
                    activeRequiredByCard,
                    activeSelection,
                    ResolveParallelism());
            }
        }
        else
        {
            activeSelection = RefinePartialSelection(
                orderedReducedSellers,
                activeRequiredByCard,
                beamResult.SelectedOrderedSellerIndices,
                reducedExactResult.SelectedOrderedSellerIndices);
        }

        selectedSellerNames.UnionWith(activeSelection);

        return BuildResult(snapshot.ScopedMarketData, selectedSellerNames, knownUncoveredCardKeys);
    }

    private static PurchaseOptimizationResult BuildResult(
        IReadOnlyList<MarketCardData> scopedMarketData,
        IReadOnlyCollection<string> selectedSellerNames,
        IReadOnlySet<string> knownUncoveredCardKeys)
    {
        var assignments = BuildAssignments(
            scopedMarketData,
            selectedSellerNames,
            out var uncoveredCards,
            out var totalCardsCost);

        var uncoveredCardKeys = uncoveredCards
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        uncoveredCardKeys.UnionWith(knownUncoveredCardKeys);

        var selectedSellers = selectedSellerNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PurchaseOptimizationResult
        {
            SelectedSellerNames = selectedSellers,
            Assignments = assignments,
            SellerCount = selectedSellers.Count,
            CardsTotalPrice = totalCardsCost,
            UncoveredCardKeys = uncoveredCardKeys
        };
    }

    private static void AddUncoveredCardKeys(
        IReadOnlyList<MarketCardData> marketData,
        IReadOnlyList<int> requiredByCard,
        ISet<string> uncoveredCardKeys)
    {
        for (var cardIndex = 0; cardIndex < marketData.Count; cardIndex++)
        {
            if (requiredByCard[cardIndex] > 0)
            {
                uncoveredCardKeys.Add(ResolveCardKey(marketData[cardIndex].Target));
            }
        }
    }

    private static int ResolveRemainingRequiredQuantity(
        string cardKey,
        int desiredQuantity,
        IReadOnlyDictionary<string, int> remainingRequiredByCardKey)
    {
        if (remainingRequiredByCardKey.TryGetValue(cardKey, out var remaining))
        {
            return Math.Max(0, remaining);
        }

        return Math.Max(0, desiredQuantity);
    }

    private static string ResolveCardKey(ScrapingTarget target)
        => string.IsNullOrWhiteSpace(target.RequestKey)
            ? target.ProductUrl
            : target.RequestKey;

    private static RuntimeSettings ResolveRuntimeSettings(PurchaseOptimizerOptions options)
    {
        var candidatePoolMax = Math.Clamp(options.CandidatePoolMax, 1, 80);
        var candidatePoolMin = Math.Clamp(options.CandidatePoolMin, 1, candidatePoolMax);

        return new RuntimeSettings(
            CandidateTopCheapestPerCard: Math.Max(1, options.CandidateTopCheapestPerCard),
            CandidateTopEffectivePerCard: Math.Max(1, options.CandidateTopEffectivePerCard),
            CandidatePoolMin: candidatePoolMin,
            CandidatePoolMax: candidatePoolMax,
            BeamWidth: Math.Clamp(options.BeamWidth, 200, 500),
            BeamAlpha: Math.Max(0m, options.BeamAlpha),
            BeamBeta: Math.Max(0m, options.BeamBeta),
            ExactMaxK: Math.Max(1, options.ExactMaxK),
            EnableFinalCostRefine: options.EnableFinalCostRefine,
            SolverTimeBudgetMinutes: Math.Max(1, options.SolverTimeBudgetMinutes));
    }

    private static int ResolveParallelism()
        => Math.Max(1, Environment.ProcessorCount / 2);

    private static List<CanonicalSeller> BuildCanonicalSellers(
        IReadOnlyList<MarketCardData> marketData,
        int[] requiredByCard,
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName)
    {
        var sellerIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var accumulators = new List<SellerAccumulator>();
        var cardCount = marketData.Count;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            foreach (var offer in marketData[cardIndex].Offers)
            {
                if (!sellerIndexByName.TryGetValue(offer.SellerName, out var sellerIndex))
                {
                    sellerIndex = accumulators.Count;
                    sellerIndexByName[offer.SellerName] = sellerIndex;
                    accumulators.Add(new SellerAccumulator(
                        sellerIndex,
                        offer.SellerName,
                        cardCount));
                }

                accumulators[sellerIndex].AddOffer(cardIndex, offer);
            }
        }

        var sellers = new List<CanonicalSeller>(accumulators.Count);

        foreach (var accumulator in accumulators)
        {
            var profiles = new SellerCardProfile?[cardCount];
            var qtyByCard = new int[cardCount];
            var activeCards = new List<int>(cardCount);

            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                var requiredQuantity = requiredByCard[cardIndex];
                var profile = BuildSellerCardProfile(
                    accumulator.GetOffers(cardIndex),
                    requiredQuantity);

                if (profile is null || profile.QtyUsable <= 0)
                {
                    continue;
                }

                profiles[cardIndex] = profile;
                qtyByCard[cardIndex] = profile.QtyUsable;
                activeCards.Add(cardIndex);
            }

            sellers.Add(new CanonicalSeller(
                accumulator.OriginalOrder,
                accumulator.SellerName,
                ResolveSellerFixedCost(accumulator.SellerName, fixedCostBySellerName),
                profiles,
                qtyByCard,
                [.. activeCards]));
        }

        return sellers;
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

    private static SellerCardProfile? BuildSellerCardProfile(
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
                prefixCosts[covered] = AddCost(prefixCosts[covered - 1], offer.Price);
            }
        }

        return covered == qtyUsable
            ? new SellerCardProfile(prefixCosts)
            : null;
    }

    private static IReadOnlyList<CanonicalSeller> OrderSellersForSearch(
        IReadOnlyList<CanonicalSeller> sellers,
        int[] requiredByCard,
        int cardCount)
    {
        return sellers
            .OrderByDescending(seller => CoverageScore(seller, requiredByCard, cardCount))
            .ThenByDescending(seller => seller.ActiveCardCount)
            .ThenBy(seller => seller.SellerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(seller => seller.OriginalOrder)
            .ToList();
    }

    private static decimal CoverageScore(
        CanonicalSeller seller,
        int[] requiredByCard,
        int cardCount)
    {
        var score = 0m;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            var qty = seller.QtyByCard[cardIndex];
            if (qty <= 0)
            {
                continue;
            }

            score += Math.Min(qty, requiredByCard[cardIndex]);
            score += 0.1m;
        }

        return score;
    }

    private static HashSet<string> RefineSelectionForFixedSellerCountOnCanonical(
        IReadOnlyList<CanonicalSeller> candidateSellers,
        int[] requiredByCard,
        IReadOnlyCollection<string> selectedSellerNames,
        int effectiveParallelism)
    {
        var targetSellerCount = selectedSellerNames.Count;
        if (targetSellerCount <= 0)
        {
            return [];
        }

        var cardCount = requiredByCard.Length;
        if (requiredByCard.Sum() <= 0)
        {
            return selectedSellerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var orderedSellers = OrderSellersForSearch(candidateSellers, requiredByCard, cardCount).ToList();
        if (orderedSellers.Count < targetSellerCount)
        {
            return selectedSellerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var sellerCardQty = BuildSellerCardQtyMatrix(orderedSellers, cardCount);
        var suffixCoverage = BuildSuffixCoverageMatrix(sellerCardQty, orderedSellers.Count, cardCount);
        var suffixMinCosts = BuildSuffixMinimumCosts(orderedSellers, requiredByCard, orderedSellers.Count, cardCount);

        var initialSelection = orderedSellers
            .Select((seller, index) => new { seller.SellerName, Index = index })
            .Where(item => selectedSellerNames.Contains(item.SellerName))
            .Select(item => item.Index)
            .OrderBy(index => index)
            .ToList();

        if (initialSelection.Count != targetSellerCount)
        {
            return selectedSellerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var initialUpperBoundCost = CalculateExactCostForSelection(
            orderedSellers,
            requiredByCard,
            cardCount,
            initialSelection);

        var bestSelection = FindBestSelectionForFixedK(
            targetSellerCount,
            requiredByCard,
            orderedSellers,
            sellerCardQty,
            suffixCoverage,
            suffixMinCosts,
            orderedSellers.Count,
            cardCount,
            effectiveParallelism,
            initialUpperBoundCost,
            initialSelection);

        if (bestSelection is null || IsInfinite(bestSelection.TotalCost))
        {
            return selectedSellerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return bestSelection.SelectedOrderedSellerIndices
            .Select(index => orderedSellers[index].SellerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int[,] BuildSellerCardQtyMatrix(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int cardCount)
    {
        var qtyBySellerAndCard = new int[orderedSellers.Count, cardCount];

        for (var sellerIndex = 0; sellerIndex < orderedSellers.Count; sellerIndex++)
        {
            foreach (var cardIndex in orderedSellers[sellerIndex].ActiveCards)
            {
                qtyBySellerAndCard[sellerIndex, cardIndex] = orderedSellers[sellerIndex].QtyByCard[cardIndex];
            }
        }

        return qtyBySellerAndCard;
    }

    private static int[,] BuildSuffixCoverageMatrix(
        int[,] sellerCardQty,
        int sellerCount,
        int cardCount)
    {
        var suffix = new int[sellerCount + 1, cardCount];

        for (var sellerIndex = sellerCount - 1; sellerIndex >= 0; sellerIndex--)
        {
            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                suffix[sellerIndex, cardIndex] = suffix[sellerIndex + 1, cardIndex] + sellerCardQty[sellerIndex, cardIndex];
            }
        }

        return suffix;
    }

    private static decimal[,,] BuildSuffixMinimumCosts(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int sellerCount,
        int cardCount)
    {
        var maxRequiredQty = requiredByCard.Length == 0 ? 0 : requiredByCard.Max();
        var suffixMinCosts = new decimal[sellerCount + 1, cardCount, maxRequiredQty + 1];

        for (var sellerIndex = 0; sellerIndex <= sellerCount; sellerIndex++)
        {
            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                var requiredQty = requiredByCard[cardIndex];
                for (var qty = 0; qty <= requiredQty; qty++)
                {
                    suffixMinCosts[sellerIndex, cardIndex, qty] = qty == 0 ? 0m : InfiniteCost;
                }
            }
        }

        for (var sellerIndex = sellerCount - 1; sellerIndex >= 0; sellerIndex--)
        {
            var seller = orderedSellers[sellerIndex];

            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                var requiredQty = requiredByCard[cardIndex];
                for (var qty = 0; qty <= requiredQty; qty++)
                {
                    suffixMinCosts[sellerIndex, cardIndex, qty] = suffixMinCosts[sellerIndex + 1, cardIndex, qty];
                }

                var profile = seller.CardProfiles[cardIndex];
                if (profile is null)
                {
                    continue;
                }

                var maxTake = Math.Min(profile.QtyUsable, requiredQty);
                for (var currentQty = 0; currentQty <= requiredQty; currentQty++)
                {
                    var baseCost = suffixMinCosts[sellerIndex + 1, cardIndex, currentQty];
                    if (IsInfinite(baseCost))
                    {
                        continue;
                    }

                    for (var take = 1; take <= maxTake && currentQty + take <= requiredQty; take++)
                    {
                        var newQty = currentQty + take;
                        var candidateCost = AddCost(baseCost, profile.PrefixCosts[take]);
                        if (candidateCost + CostEpsilon < suffixMinCosts[sellerIndex, cardIndex, newQty])
                        {
                            suffixMinCosts[sellerIndex, cardIndex, newQty] = candidateCost;
                        }
                    }
                }
            }
        }

        return suffixMinCosts;
    }

    private static List<int> FindImpossibleCardIndices(
        int[] requiredByCard,
        int[,] sellerCardQty,
        int sellerCount)
    {
        var impossible = new List<int>();
        var cardCount = requiredByCard.Length;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            if (requiredByCard[cardIndex] <= 0)
            {
                continue;
            }

            var totalQty = 0;
            for (var sellerIndex = 0; sellerIndex < sellerCount; sellerIndex++)
            {
                totalQty += sellerCardQty[sellerIndex, cardIndex];
            }

            if (totalQty < requiredByCard[cardIndex])
            {
                impossible.Add(cardIndex);
            }
        }

        return impossible;
    }

    private static int CalculateLowerBoundSellerCount(
        int[] requiredQuantities,
        int[,] sellerCardQty,
        int cardCount,
        int sellerCount)
    {
        var lowerBound = 1;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            var remaining = requiredQuantities[cardIndex];
            if (remaining <= 0)
            {
                continue;
            }

            var sellerQtyForCard = new List<int>(sellerCount);

            for (var sellerIndex = 0; sellerIndex < sellerCount; sellerIndex++)
            {
                var qty = sellerCardQty[sellerIndex, cardIndex];
                if (qty > 0)
                {
                    sellerQtyForCard.Add(qty);
                }
            }

            sellerQtyForCard.Sort((left, right) => right.CompareTo(left));

            var sellersNeeded = 0;
            foreach (var qty in sellerQtyForCard)
            {
                remaining -= qty;
                sellersNeeded++;
                if (remaining <= 0)
                {
                    break;
                }
            }

            lowerBound = Math.Max(lowerBound, sellersNeeded);
        }

        return lowerBound;
    }

    private static SelectionResult? FindBestSelectionForFixedK(
        int targetSellerCount,
        int[] requiredByCard,
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[,] sellerCardQty,
        int[,] suffixCoverage,
        decimal[,,] suffixMinCosts,
        int sellerCount,
        int cardCount,
        int effectiveParallelism,
        decimal initialUpperBoundCost,
        IReadOnlyList<int>? initialSelection)
    {
        if (targetSellerCount < 0 || targetSellerCount > sellerCount)
        {
            return null;
        }

        if (targetSellerCount == 0)
        {
            var state = new CostSearchState(
                targetSellerCount,
                requiredByCard,
                orderedSellers,
                sellerCardQty,
                suffixCoverage,
                suffixMinCosts,
                sellerCount,
                cardCount,
                Array.Empty<int>(),
                0,
                initialUpperBoundCost,
                initialSelection);

            state.Search();
            return state.BestResult;
        }

        if (effectiveParallelism <= 1 || sellerCount - targetSellerCount + 1 <= 1)
        {
            var state = new CostSearchState(
                targetSellerCount,
                requiredByCard,
                orderedSellers,
                sellerCardQty,
                suffixCoverage,
                suffixMinCosts,
                sellerCount,
                cardCount,
                Array.Empty<int>(),
                0,
                initialUpperBoundCost,
                initialSelection);

            state.Search();
            return state.BestResult;
        }

        var partitionFirstSellerIndices = Enumerable.Range(0, sellerCount - targetSellerCount + 1).ToArray();
        var sync = new object();
        SelectionResult? bestResult = initialSelection is null || IsInfinite(initialUpperBoundCost)
            ? null
            : new SelectionResult(initialSelection.ToArray(), initialUpperBoundCost);

        Parallel.ForEach(
            partitionFirstSellerIndices,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = effectiveParallelism
            },
            firstSellerIndex =>
            {
                var initialSelected = new[] { firstSellerIndex };
                var state = new CostSearchState(
                    targetSellerCount,
                    requiredByCard,
                    orderedSellers,
                    sellerCardQty,
                    suffixCoverage,
                    suffixMinCosts,
                    sellerCount,
                    cardCount,
                    initialSelected,
                    firstSellerIndex + 1,
                    initialUpperBoundCost,
                    initialSelection);

                state.Search();

                lock (sync)
                {
                    if (state.BestResult is not null && IsBetterCandidate(state.BestResult, bestResult))
                    {
                        bestResult = state.BestResult;
                    }
                }
            });

        return bestResult;
    }

    private static decimal CalculateExactCostForSelection(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> selectedOrderedSellerIndices)
    {
        var totalCost = 0m;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            var requiredQty = requiredByCard[cardIndex];
            if (requiredQty <= 0)
            {
                continue;
            }

            var dp = new decimal[requiredQty + 1];
            Array.Fill(dp, InfiniteCost);
            dp[0] = 0m;

            foreach (var sellerIndex in selectedOrderedSellerIndices)
            {
                var profile = orderedSellers[sellerIndex].CardProfiles[cardIndex];
                if (profile is null)
                {
                    continue;
                }

                var next = (decimal[])dp.Clone();
                var maxTake = Math.Min(profile.QtyUsable, requiredQty);

                for (var currentQty = 0; currentQty <= requiredQty; currentQty++)
                {
                    var baseCost = dp[currentQty];
                    if (IsInfinite(baseCost))
                    {
                        continue;
                    }

                    for (var take = 1; take <= maxTake && currentQty + take <= requiredQty; take++)
                    {
                        var newQty = currentQty + take;
                        var candidateCost = AddCost(baseCost, profile.PrefixCosts[take]);
                        if (candidateCost + CostEpsilon < next[newQty])
                        {
                            next[newQty] = candidateCost;
                        }
                    }
                }

                dp = next;
            }

            if (IsInfinite(dp[requiredQty]))
            {
                return InfiniteCost;
            }

            totalCost = AddCost(totalCost, dp[requiredQty]);
        }

        return AddCost(totalCost, CalculateSelectedSellersFixedCost(orderedSellers, selectedOrderedSellerIndices));
    }

    private static decimal CalculateSelectedSellersFixedCost(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        IReadOnlyList<int> selectedOrderedSellerIndices)
    {
        var totalFixedCost = 0m;

        for (var index = 0; index < selectedOrderedSellerIndices.Count; index++)
        {
            totalFixedCost = AddCost(totalFixedCost, orderedSellers[selectedOrderedSellerIndices[index]].FixedCost);
        }

        return totalFixedCost;
    }

    private static HashSet<string> RefinePartialSelection(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlyList<int> beamSelection,
        IReadOnlyList<int> reducedExactSelection)
    {
        var cardCount = requiredByCard.Length;
        SelectionCoverageQuality? bestQuality = null;

        var primarySeed = EvaluateBetterPartialSeed(
            orderedSellers,
            requiredByCard,
            cardCount,
            beamSelection,
            reducedExactSelection);
        bestQuality = ConsiderPartialCandidate(
            bestQuality,
            orderedSellers,
            requiredByCard,
            cardCount,
            primarySeed);

        var provisionalTargetSellerCount = Math.Min(4, orderedSellers.Count);
        if (provisionalTargetSellerCount > 0)
        {
            var provisionalSelection = BuildGreedyProvisionalSelection(
                orderedSellers,
                requiredByCard,
                cardCount,
                provisionalTargetSellerCount);
            bestQuality = ConsiderPartialCandidate(
                bestQuality,
                orderedSellers,
                requiredByCard,
                cardCount,
                provisionalSelection);
        }

        return bestQuality is null
            ? []
            : PolishPartialSelectionByObjective(
                orderedSellers,
                requiredByCard,
                cardCount,
                bestQuality.SelectedOrderedSellerIndices)
                .Select(index => orderedSellers[index].SellerName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<int> EvaluateBetterPartialSeed(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> leftSelection,
        IReadOnlyList<int> rightSelection)
    {
        var leftQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            leftSelection);
        var rightQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            rightSelection);

        return IsBetterCoverageCandidate(rightQuality, leftQuality)
            ? rightQuality.SelectedOrderedSellerIndices
            : leftQuality.SelectedOrderedSellerIndices;
    }

    private static SelectionCoverageQuality? ConsiderPartialCandidate(
        SelectionCoverageQuality? currentBest,
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> selection)
    {
        if (selection.Count == 0)
        {
            return currentBest;
        }

        var candidateQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            selection);
        if (IsBetterCoverageCandidate(candidateQuality, currentBest))
        {
            currentBest = candidateQuality;
        }

        var swappedSelection = RefineSelectionWithSingleSwap(
            orderedSellers,
            requiredByCard,
            cardCount,
            selection);
        var swappedQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            swappedSelection);
        if (IsBetterCoverageCandidate(swappedQuality, currentBest))
        {
            currentBest = swappedQuality;
        }

        var prunedSelection = PruneRedundantSellers(
            orderedSellers,
            requiredByCard,
            cardCount,
            swappedSelection);
        var prunedQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            prunedSelection);
        if (IsBetterCoverageCandidate(prunedQuality, currentBest))
        {
            currentBest = prunedQuality;
        }

        return currentBest;
    }

    private static List<int> BuildGreedyProvisionalSelection(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        int targetSellerCount)
    {
        var selected = new List<int>(targetSellerCount);
        var currentQuality = EvaluateSelectionCoverage(orderedSellers, requiredByCard, cardCount, selected);

        while (selected.Count < targetSellerCount)
        {
            SelectionCoverageQuality? bestCandidate = null;

            for (var sellerIndex = 0; sellerIndex < orderedSellers.Count; sellerIndex++)
            {
                if (selected.Contains(sellerIndex))
                {
                    continue;
                }

                var candidateSelection = new List<int>(selected.Count + 1);
                candidateSelection.AddRange(selected);
                candidateSelection.Add(sellerIndex);

                var candidateQuality = EvaluateSelectionCoverage(
                    orderedSellers,
                    requiredByCard,
                    cardCount,
                    candidateSelection);

                if (IsBetterCoverageCandidate(candidateQuality, bestCandidate))
                {
                    bestCandidate = candidateQuality;
                }
            }

            if (bestCandidate is null || !IsBetterCoverageCandidate(bestCandidate, currentQuality))
            {
                break;
            }

            selected = bestCandidate.SelectedOrderedSellerIndices.ToList();
            currentQuality = bestCandidate;

            if (currentQuality.IsFullyCovered)
            {
                break;
            }
        }

        return selected;
    }

    private static List<int> RefineSelectionWithSingleSwap(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> initialSelection)
    {
        var currentSelection = initialSelection
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        if (currentSelection.Count <= 1)
        {
            return currentSelection;
        }

        var currentQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            currentSelection);

        while (true)
        {
            SelectionCoverageQuality? bestSwapQuality = null;

            for (var selectedOffset = 0; selectedOffset < currentSelection.Count; selectedOffset++)
            {
                for (var candidateIndex = 0; candidateIndex < orderedSellers.Count; candidateIndex++)
                {
                    if (currentSelection.Contains(candidateIndex))
                    {
                        continue;
                    }

                    var swapped = new List<int>(currentSelection);
                    swapped[selectedOffset] = candidateIndex;
                    swapped = swapped
                        .Distinct()
                        .OrderBy(index => index)
                        .ToList();

                    if (swapped.Count != currentSelection.Count)
                    {
                        continue;
                    }

                    var swappedQuality = EvaluateSelectionCoverage(
                        orderedSellers,
                        requiredByCard,
                        cardCount,
                        swapped);

                    if (!IsBetterCoverageCandidate(swappedQuality, currentQuality))
                    {
                        continue;
                    }

                    if (IsBetterCoverageCandidate(swappedQuality, bestSwapQuality))
                    {
                        bestSwapQuality = swappedQuality;
                    }
                }
            }

            if (bestSwapQuality is null)
            {
                break;
            }

            currentQuality = bestSwapQuality;
            currentSelection = bestSwapQuality.SelectedOrderedSellerIndices.ToList();
        }

        return currentSelection;
    }

    private static List<int> PruneRedundantSellers(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> initialSelection)
    {
        var currentSelection = initialSelection
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        if (currentSelection.Count <= 1)
        {
            return currentSelection;
        }

        var currentQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            currentSelection);

        while (true)
        {
            SelectionCoverageQuality? bestPrunedQuality = null;

            for (var selectedOffset = 0; selectedOffset < currentSelection.Count; selectedOffset++)
            {
                var pruned = new List<int>(currentSelection);
                pruned.RemoveAt(selectedOffset);

                var prunedQuality = EvaluateSelectionCoverage(
                    orderedSellers,
                    requiredByCard,
                    cardCount,
                    pruned);

                if (!IsBetterCoverageCandidate(prunedQuality, currentQuality))
                {
                    continue;
                }

                if (IsBetterCoverageCandidate(prunedQuality, bestPrunedQuality))
                {
                    bestPrunedQuality = prunedQuality;
                }
            }

            if (bestPrunedQuality is null)
            {
                break;
            }

            currentQuality = bestPrunedQuality;
            currentSelection = bestPrunedQuality.SelectedOrderedSellerIndices.ToList();
        }

        return currentSelection;
    }

    private static SelectionCoverageQuality EvaluateSelectionCoverage(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> selectedIndices)
    {
        var selectedOrdered = selectedIndices
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        var coveredByCard = new int[cardCount];
        foreach (var sellerIndex in selectedOrdered)
        {
            foreach (var cardIndex in orderedSellers[sellerIndex].ActiveCards)
            {
                coveredByCard[cardIndex] += orderedSellers[sellerIndex].QtyByCard[cardIndex];
            }
        }

        var fullyCoveredCards = 0;
        var coveredUnits = 0;
        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            var requiredQty = requiredByCard[cardIndex];
            if (requiredQty <= 0)
            {
                continue;
            }

            var coveredQty = Math.Min(requiredQty, coveredByCard[cardIndex]);
            coveredUnits += coveredQty;

            if (coveredByCard[cardIndex] >= requiredQty)
            {
                fullyCoveredCards++;
            }
        }

        var coveredCost = AddCost(
            CalculateCostForCoveredUnits(
                orderedSellers,
                requiredByCard,
                cardCount,
                selectedOrdered,
                coveredByCard),
            CalculateSelectedSellersFixedCost(orderedSellers, selectedOrdered));

        return new SelectionCoverageQuality(
            selectedOrdered,
            coveredByCard,
            fullyCoveredCards,
            coveredUnits,
            coveredCost,
            IsFullyCovered(requiredByCard, coveredByCard, cardCount));
    }

    private static decimal CalculateCostForCoveredUnits(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> selectedOrderedSellerIndices,
        int[] coveredByCard)
    {
        var totalCost = 0m;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            var targetQty = Math.Min(requiredByCard[cardIndex], coveredByCard[cardIndex]);
            if (targetQty <= 0)
            {
                continue;
            }

            var dp = new decimal[targetQty + 1];
            Array.Fill(dp, InfiniteCost);
            dp[0] = 0m;

            foreach (var sellerIndex in selectedOrderedSellerIndices)
            {
                var profile = orderedSellers[sellerIndex].CardProfiles[cardIndex];
                if (profile is null)
                {
                    continue;
                }

                var next = (decimal[])dp.Clone();
                var maxTake = Math.Min(profile.QtyUsable, targetQty);

                for (var currentQty = 0; currentQty <= targetQty; currentQty++)
                {
                    var baseCost = dp[currentQty];
                    if (IsInfinite(baseCost))
                    {
                        continue;
                    }

                    for (var take = 1; take <= maxTake && currentQty + take <= targetQty; take++)
                    {
                        var newQty = currentQty + take;
                        var candidateCost = AddCost(baseCost, profile.PrefixCosts[take]);
                        if (candidateCost + CostEpsilon < next[newQty])
                        {
                            next[newQty] = candidateCost;
                        }
                    }
                }

                dp = next;
            }

            if (IsInfinite(dp[targetQty]))
            {
                return InfiniteCost;
            }

            totalCost = AddCost(totalCost, dp[targetQty]);
        }

        return totalCost;
    }

    private static bool IsFullyCovered(int[] requiredByCard, int[] coveredByCard, int cardCount)
    {
        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            if (requiredByCard[cardIndex] > coveredByCard[cardIndex])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBetterCoverageCandidate(
        SelectionCoverageQuality candidate,
        SelectionCoverageQuality? current)
    {
        if (current is null)
        {
            return true;
        }

        var coveredCardsComparison = candidate.FullyCoveredCards.CompareTo(current.FullyCoveredCards);
        if (coveredCardsComparison != 0)
        {
            return coveredCardsComparison > 0;
        }

        var coveredUnitsComparison = candidate.CoveredUnits.CompareTo(current.CoveredUnits);
        if (coveredUnitsComparison != 0)
        {
            return coveredUnitsComparison > 0;
        }

        if (candidate.CoveredCost + CostEpsilon < current.CoveredCost)
        {
            return true;
        }

        if (current.CoveredCost + CostEpsilon < candidate.CoveredCost)
        {
            return false;
        }

        return CompareSelectionsLexicographically(
            candidate.SelectedOrderedSellerIndices,
            current.SelectedOrderedSellerIndices) < 0;
    }

    private static IReadOnlyList<int> PolishPartialSelectionByObjective(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> initialSelection)
    {
        var currentSelection = initialSelection
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        if (currentSelection.Count == 0)
        {
            return currentSelection;
        }

        var currentObjective = EvaluatePartialSelectionObjective(
            orderedSellers,
            requiredByCard,
            cardCount,
            currentSelection);

        while (true)
        {
            PartialSelectionObjective? bestCandidate = null;
            var currentSelectionSet = currentSelection.ToHashSet();

            if (currentSelection.Count > 1)
            {
                for (var selectedOffset = 0; selectedOffset < currentSelection.Count; selectedOffset++)
                {
                    var pruned = new List<int>(currentSelection);
                    pruned.RemoveAt(selectedOffset);

                    var prunedObjective = EvaluatePartialSelectionObjective(
                        orderedSellers,
                        requiredByCard,
                        cardCount,
                        pruned);

                    if (!IsBetterPartialObjectiveCandidate(prunedObjective, currentObjective))
                    {
                        continue;
                    }

                    if (IsBetterPartialObjectiveCandidate(prunedObjective, bestCandidate))
                    {
                        bestCandidate = prunedObjective;
                    }
                }
            }

            for (var selectedOffset = 0; selectedOffset < currentSelection.Count; selectedOffset++)
            {
                for (var candidateIndex = 0; candidateIndex < orderedSellers.Count; candidateIndex++)
                {
                    if (currentSelectionSet.Contains(candidateIndex))
                    {
                        continue;
                    }

                    var swapped = new List<int>(currentSelection);
                    swapped[selectedOffset] = candidateIndex;
                    swapped = swapped
                        .Distinct()
                        .OrderBy(index => index)
                        .ToList();

                    if (swapped.Count != currentSelection.Count)
                    {
                        continue;
                    }

                    var swappedObjective = EvaluatePartialSelectionObjective(
                        orderedSellers,
                        requiredByCard,
                        cardCount,
                        swapped);

                    if (!IsBetterPartialObjectiveCandidate(swappedObjective, currentObjective))
                    {
                        continue;
                    }

                    if (IsBetterPartialObjectiveCandidate(swappedObjective, bestCandidate))
                    {
                        bestCandidate = swappedObjective;
                    }
                }
            }

            if (bestCandidate is null)
            {
                break;
            }

            currentObjective = bestCandidate;
            currentSelection = bestCandidate.SelectedOrderedSellerIndices.ToList();
        }

        return currentSelection;
    }

    private static PartialSelectionObjective EvaluatePartialSelectionObjective(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount,
        IReadOnlyList<int> selection)
    {
        var coverageQuality = EvaluateSelectionCoverage(
            orderedSellers,
            requiredByCard,
            cardCount,
            selection);

        var uncoveredCardIndices = new List<int>();
        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            if (requiredByCard[cardIndex] > coverageQuality.CoveredByCard[cardIndex])
            {
                uncoveredCardIndices.Add(cardIndex);
            }
        }

        return new PartialSelectionObjective(
            coverageQuality.SelectedOrderedSellerIndices,
            uncoveredCardIndices,
            coverageQuality.CoveredCost);
    }

    private static bool IsBetterPartialObjectiveCandidate(
        PartialSelectionObjective candidate,
        PartialSelectionObjective? current)
    {
        if (current is null)
        {
            return true;
        }

        var uncoveredCountComparison = candidate.UncoveredCardIndices.Count.CompareTo(current.UncoveredCardIndices.Count);
        if (uncoveredCountComparison != 0)
        {
            return uncoveredCountComparison < 0;
        }

        var uncoveredIndicesComparison = CompareSelectionsLexicographically(
            candidate.UncoveredCardIndices,
            current.UncoveredCardIndices);
        if (uncoveredIndicesComparison != 0)
        {
            return uncoveredIndicesComparison < 0;
        }

        if (candidate.TotalCost + CostEpsilon < current.TotalCost)
        {
            return true;
        }

        if (current.TotalCost + CostEpsilon < candidate.TotalCost)
        {
            return false;
        }

        return CompareSelectionsLexicographically(
            candidate.SelectedOrderedSellerIndices,
            current.SelectedOrderedSellerIndices) < 0;
    }

    private static List<PurchaseAssignment> BuildAssignments(
        IReadOnlyList<MarketCardData> marketData,
        IReadOnlyCollection<string> selectedSellerNames,
        out List<string> uncoveredCards,
        out decimal totalCardsCost)
    {
        var selectedSet = new HashSet<string>(selectedSellerNames, StringComparer.OrdinalIgnoreCase);
        var assignments = new List<PurchaseAssignment>();
        uncoveredCards = [];
        totalCardsCost = 0m;

        foreach (var card in marketData)
        {
            var quantityToBuy = Math.Max(0, card.Target.DesiredQuantity);
            if (quantityToBuy <= 0)
            {
                continue;
            }

            var selectedOffers = card.Offers
                .Where(offer => selectedSet.Contains(offer.SellerName))
                .OrderBy(offer => offer.Price)
                .ThenBy(offer => offer.SellerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(offer => offer.ProductUrl, StringComparer.OrdinalIgnoreCase)
                .ThenBy(offer => offer.CardName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(offer => offer.SetName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var covered = 0;

            foreach (var offer in selectedOffers)
            {
                if (covered >= quantityToBuy)
                {
                    break;
                }

                var take = Math.Min(offer.AvailableQuantity, quantityToBuy - covered);
                if (take <= 0)
                {
                    continue;
                }

                var totalPrice = offer.Price * take;
                assignments.Add(new PurchaseAssignment(
                    SellerName: offer.SellerName,
                    ProductUrl: offer.ProductUrl,
                    CardName: offer.CardName,
                    SetName: offer.SetName,
                    Quantity: take,
                    UnitPrice: offer.Price,
                    TotalPrice: totalPrice));

                totalCardsCost = AddCost(totalCardsCost, totalPrice);
                covered += take;
            }

            if (covered < quantityToBuy)
            {
                uncoveredCards.Add(ResolveCardKey(card.Target));
            }
        }

        return assignments;
    }

    private static bool IsBetterCandidate(
        SelectionResult candidate,
        SelectionResult? currentBest)
    {
        if (currentBest is null)
        {
            return true;
        }

        var costComparison = candidate.TotalCost.CompareTo(currentBest.TotalCost);
        if (costComparison < 0)
        {
            return true;
        }

        if (costComparison > 0)
        {
            return false;
        }

        return CompareSelectionsLexicographically(
            candidate.SelectedOrderedSellerIndices,
            currentBest.SelectedOrderedSellerIndices) < 0;
    }

    private static int CompareSelectionsLexicographically(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right)
    {
        var count = Math.Min(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            var compare = left[index].CompareTo(right[index]);
            if (compare != 0)
            {
                return compare;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static int CompareSelectionsLexicographically(
        int[] left,
        int leftCount,
        IReadOnlyList<int> right)
    {
        var count = Math.Min(leftCount, right.Count);
        for (var index = 0; index < count; index++)
        {
            var compare = left[index].CompareTo(right[index]);
            if (compare != 0)
            {
                return compare;
            }
        }

        return leftCount.CompareTo(right.Count);
    }

    private static decimal AddCost(decimal left, decimal right)
    {
        if (IsInfinite(left) || IsInfinite(right))
        {
            return InfiniteCost;
        }

        if (left > InfiniteCost - right)
        {
            return InfiniteCost;
        }

        return left + right;
    }

    private static bool IsInfinite(decimal value)
        => value >= InfiniteCost;

    private sealed class CandidatePoolBuilder
    {
        public HashSet<string> BuildCandidateSellerNames(
            IReadOnlyList<CanonicalSeller> sellers,
            int[] requiredByCard,
            IReadOnlyCollection<string> alwaysKeepSellerNames,
            RuntimeSettings settings)
        {
            var candidateSellerNames = new HashSet<string>(
                alwaysKeepSellerNames,
                StringComparer.OrdinalIgnoreCase);

            if (sellers.Count == 0)
            {
                return candidateSellerNames;
            }

            var topHitsBySeller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rareProviderSellerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sellerByName = sellers.ToDictionary(seller => seller.SellerName, StringComparer.OrdinalIgnoreCase);

            for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
            {
                var requiredQty = requiredByCard[cardIndex];
                if (requiredQty <= 0)
                {
                    continue;
                }

                var providers = sellers
                    .Where(seller => seller.QtyByCard[cardIndex] > 0 && seller.CardProfiles[cardIndex] is not null)
                    .Select(seller =>
                    {
                        var unitPrice = seller.CardProfiles[cardIndex]!.PrefixCosts[1];
                        var usefulUnits = Math.Max(1, Math.Min(requiredQty, seller.QtyByCard[cardIndex]));
                        var effectiveCost = unitPrice + (seller.FixedCost / usefulUnits);
                        return new CardProvider(seller, unitPrice, effectiveCost, usefulUnits);
                    })
                    .ToList();

                if (providers.Count == 0)
                {
                    continue;
                }

                if (providers.Count <= 2)
                {
                    foreach (var provider in providers)
                    {
                        candidateSellerNames.Add(provider.Seller.SellerName);
                        rareProviderSellerNames.Add(provider.Seller.SellerName);
                    }
                }

                foreach (var provider in providers
                    .OrderBy(provider => provider.UnitPrice)
                    .ThenBy(provider => provider.Seller.SellerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(provider => provider.Seller.OriginalOrder)
                    .Take(settings.CandidateTopCheapestPerCard))
                {
                    candidateSellerNames.Add(provider.Seller.SellerName);
                    topHitsBySeller[provider.Seller.SellerName] = topHitsBySeller.GetValueOrDefault(provider.Seller.SellerName) + 1;
                }

                foreach (var provider in providers
                    .OrderBy(provider => provider.EffectiveCost)
                    .ThenBy(provider => provider.UnitPrice)
                    .ThenBy(provider => provider.Seller.SellerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(provider => provider.Seller.OriginalOrder)
                    .Take(settings.CandidateTopEffectivePerCard))
                {
                    candidateSellerNames.Add(provider.Seller.SellerName);
                    topHitsBySeller[provider.Seller.SellerName] = topHitsBySeller.GetValueOrDefault(provider.Seller.SellerName) + 1;
                }
            }

            foreach (var seller in sellers)
            {
                var coveredCards = CountCoveredCards(seller, requiredByCard);
                var topHits = topHitsBySeller.GetValueOrDefault(seller.SellerName);

                if (coveredCards >= 2 || topHits >= 2)
                {
                    candidateSellerNames.Add(seller.SellerName);
                }
            }

            candidateSellerNames.UnionWith(alwaysKeepSellerNames);

            var ranked = RankSellers(sellers, requiredByCard, topHitsBySeller, rareProviderSellerNames);

            if (candidateSellerNames.Count < settings.CandidatePoolMin)
            {
                foreach (var rankedSeller in ranked)
                {
                    if (candidateSellerNames.Count >= settings.CandidatePoolMin)
                    {
                        break;
                    }

                    candidateSellerNames.Add(rankedSeller.SellerName);
                }
            }

            candidateSellerNames = TrimToCapWithFeasibility(
                candidateSellerNames,
                sellers,
                requiredByCard,
                settings.CandidatePoolMax,
                alwaysKeepSellerNames,
                rareProviderSellerNames,
                ranked);

            EnsureFeasibleCoverage(
                candidateSellerNames,
                sellers,
                requiredByCard,
                settings.CandidatePoolMax,
                ranked,
                sellerByName);

            candidateSellerNames = TrimToCapWithFeasibility(
                candidateSellerNames,
                sellers,
                requiredByCard,
                settings.CandidatePoolMax,
                alwaysKeepSellerNames,
                rareProviderSellerNames,
                ranked);

            return candidateSellerNames;
        }

        private static List<RankedSeller> RankSellers(
            IReadOnlyList<CanonicalSeller> sellers,
            int[] requiredByCard,
            IReadOnlyDictionary<string, int> topHitsBySeller,
            IReadOnlySet<string> rareProviderSellerNames)
        {
            return sellers
                .Select(seller =>
                {
                    var coverageCards = CountCoveredCards(seller, requiredByCard);
                    var coverageUnits = 0;
                    var oneUnitCost = seller.FixedCost;

                    foreach (var cardIndex in seller.ActiveCards)
                    {
                        if (requiredByCard[cardIndex] <= 0)
                        {
                            continue;
                        }

                        coverageUnits += Math.Min(requiredByCard[cardIndex], seller.QtyByCard[cardIndex]);
                        var profile = seller.CardProfiles[cardIndex];
                        if (profile is not null && profile.QtyUsable > 0)
                        {
                            oneUnitCost = AddCost(oneUnitCost, profile.PrefixCosts[1]);
                        }
                    }

                    return new RankedSeller(
                        seller.SellerName,
                        seller.OriginalOrder,
                        topHitsBySeller.GetValueOrDefault(seller.SellerName),
                        coverageCards,
                        coverageUnits,
                        rareProviderSellerNames.Contains(seller.SellerName),
                        oneUnitCost);
                })
                .OrderByDescending(item => item.IsRareProvider)
                .ThenByDescending(item => item.TopHits)
                .ThenByDescending(item => item.CoverageCards)
                .ThenByDescending(item => item.CoverageUnits)
                .ThenBy(item => item.OneUnitCost)
                .ThenBy(item => item.SellerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.OriginalOrder)
                .ToList();
        }

        private static HashSet<string> TrimToCapWithFeasibility(
            HashSet<string> candidateSellerNames,
            IReadOnlyList<CanonicalSeller> sellers,
            int[] requiredByCard,
            int maxCount,
            IReadOnlyCollection<string> alwaysKeepSellerNames,
            IReadOnlySet<string> rareProviderSellerNames,
            IReadOnlyList<RankedSeller> ranked)
        {
            if (candidateSellerNames.Count <= maxCount)
            {
                return candidateSellerNames;
            }

            var protectedSellerNames = new HashSet<string>(alwaysKeepSellerNames, StringComparer.OrdinalIgnoreCase);
            protectedSellerNames.UnionWith(rareProviderSellerNames);

            var removalOrder = ranked
                .Where(item => candidateSellerNames.Contains(item.SellerName) && !protectedSellerNames.Contains(item.SellerName))
                .OrderBy(item => item.IsRareProvider)
                .ThenBy(item => item.TopHits)
                .ThenBy(item => item.CoverageCards)
                .ThenBy(item => item.CoverageUnits)
                .ThenByDescending(item => item.OneUnitCost)
                .ThenBy(item => item.SellerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.OriginalOrder)
                .ToList();

            foreach (var removable in removalOrder)
            {
                if (candidateSellerNames.Count <= maxCount)
                {
                    break;
                }

                candidateSellerNames.Remove(removable.SellerName);
                if (!HasFeasibleCoverage(candidateSellerNames, sellers, requiredByCard))
                {
                    candidateSellerNames.Add(removable.SellerName);
                }
            }

            if (candidateSellerNames.Count > maxCount)
            {
                foreach (var removable in removalOrder)
                {
                    if (candidateSellerNames.Count <= maxCount)
                    {
                        break;
                    }

                    candidateSellerNames.Remove(removable.SellerName);
                }
            }

            if (candidateSellerNames.Count > maxCount)
            {
                var hardRemovalOrder = ranked
                    .Where(item => candidateSellerNames.Contains(item.SellerName))
                    .OrderBy(item => item.IsRareProvider)
                    .ThenBy(item => item.TopHits)
                    .ThenBy(item => item.CoverageCards)
                    .ThenBy(item => item.CoverageUnits)
                    .ThenByDescending(item => item.OneUnitCost)
                    .ThenBy(item => item.SellerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.OriginalOrder)
                    .ToList();

                foreach (var removable in hardRemovalOrder)
                {
                    if (candidateSellerNames.Count <= maxCount)
                    {
                        break;
                    }

                    candidateSellerNames.Remove(removable.SellerName);
                }
            }

            return candidateSellerNames;
        }

        private static void EnsureFeasibleCoverage(
            HashSet<string> candidateSellerNames,
            IReadOnlyList<CanonicalSeller> sellers,
            int[] requiredByCard,
            int maxCount,
            IReadOnlyList<RankedSeller> ranked,
            IReadOnlyDictionary<string, CanonicalSeller> sellerByName)
        {
            for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
            {
                var requiredQty = requiredByCard[cardIndex];
                if (requiredQty <= 0)
                {
                    continue;
                }

                while (GetTotalQty(candidateSellerNames, sellers, cardIndex) < requiredQty)
                {
                    var missingProvider = sellers
                        .Where(seller => !candidateSellerNames.Contains(seller.SellerName) && seller.QtyByCard[cardIndex] > 0)
                        .Select(seller =>
                        {
                            var unitPrice = seller.CardProfiles[cardIndex]!.PrefixCosts[1];
                            var usefulUnits = Math.Max(1, Math.Min(requiredQty, seller.QtyByCard[cardIndex]));
                            var effectiveCost = unitPrice + (seller.FixedCost / usefulUnits);
                            return new CardProvider(seller, unitPrice, effectiveCost, usefulUnits);
                        })
                        .OrderBy(provider => provider.UnitPrice)
                        .ThenBy(provider => provider.EffectiveCost)
                        .ThenBy(provider => provider.Seller.SellerName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(provider => provider.Seller.OriginalOrder)
                        .FirstOrDefault();

                    if (missingProvider is null)
                    {
                        break;
                    }

                    candidateSellerNames.Add(missingProvider.Seller.SellerName);
                }
            }

            if (candidateSellerNames.Count < maxCount)
            {
                foreach (var rankedSeller in ranked)
                {
                    if (candidateSellerNames.Count >= maxCount)
                    {
                        break;
                    }

                    candidateSellerNames.Add(rankedSeller.SellerName);
                }
            }

            if (candidateSellerNames.Count <= maxCount)
            {
                return;
            }

            var rankedByWeakness = ranked
                .Where(item => candidateSellerNames.Contains(item.SellerName))
                .OrderBy(item => item.TopHits)
                .ThenBy(item => item.CoverageCards)
                .ThenBy(item => item.CoverageUnits)
                .ThenByDescending(item => item.OneUnitCost)
                .ThenBy(item => item.SellerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.OriginalOrder)
                .ToList();

            foreach (var rankedSeller in rankedByWeakness)
            {
                if (candidateSellerNames.Count <= maxCount)
                {
                    break;
                }

                if (!sellerByName.TryGetValue(rankedSeller.SellerName, out var canonicalSeller))
                {
                    candidateSellerNames.Remove(rankedSeller.SellerName);
                    continue;
                }

                candidateSellerNames.Remove(rankedSeller.SellerName);
                if (!HasFeasibleCoverage(candidateSellerNames, sellers, requiredByCard))
                {
                    candidateSellerNames.Add(canonicalSeller.SellerName);
                }
            }
        }

        private static bool HasFeasibleCoverage(
            IReadOnlySet<string> candidateSellerNames,
            IReadOnlyList<CanonicalSeller> sellers,
            int[] requiredByCard)
        {
            for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
            {
                var requiredQty = requiredByCard[cardIndex];
                if (requiredQty <= 0)
                {
                    continue;
                }

                if (GetTotalQty(candidateSellerNames, sellers, cardIndex) < requiredQty)
                {
                    return false;
                }
            }

            return true;
        }

        private static int GetTotalQty(
            IReadOnlySet<string> candidateSellerNames,
            IReadOnlyList<CanonicalSeller> sellers,
            int cardIndex)
        {
            var total = 0;

            for (var sellerIndex = 0; sellerIndex < sellers.Count; sellerIndex++)
            {
                var seller = sellers[sellerIndex];
                if (!candidateSellerNames.Contains(seller.SellerName))
                {
                    continue;
                }

                total += seller.QtyByCard[cardIndex];
            }

            return total;
        }

        private static int CountCoveredCards(CanonicalSeller seller, int[] requiredByCard)
        {
            var coveredCards = 0;

            foreach (var cardIndex in seller.ActiveCards)
            {
                if (requiredByCard[cardIndex] > 0 && seller.QtyByCard[cardIndex] > 0)
                {
                    coveredCards++;
                }
            }

            return coveredCards;
        }

        private sealed record CardProvider(
            CanonicalSeller Seller,
            decimal UnitPrice,
            decimal EffectiveCost,
            int UsefulUnits);

        private sealed record RankedSeller(
            string SellerName,
            int OriginalOrder,
            int TopHits,
            int CoverageCards,
            int CoverageUnits,
            bool IsRareProvider,
            decimal OneUnitCost);
    }

    private sealed class BeamSearchSolver
    {
        private readonly IReadOnlyList<CanonicalSeller> _orderedSellers;
        private readonly int[] _requiredByCard;
        private readonly int _cardCount;
        private readonly RuntimeSettings _settings;

        public BeamSearchSolver(
            IReadOnlyList<CanonicalSeller> orderedSellers,
            int[] requiredByCard,
            RuntimeSettings settings)
        {
            _orderedSellers = orderedSellers;
            _requiredByCard = requiredByCard;
            _cardCount = requiredByCard.Length;
            _settings = settings;
        }

        public BeamSearchResult Run()
        {
            if (_orderedSellers.Count == 0)
            {
                return new BeamSearchResult(
                    IsFullCoverage: false,
                    SelectedOrderedSellerIndices: [],
                    TotalCost: InfiniteCost);
            }

            var cardLowerBounds = BuildCardLowerBounds();
            var emptyCoverage = new int[_cardCount];
            var emptyMetrics = BuildCoverageMetrics(emptyCoverage);
            var emptyRemainingCost = EstimateRemainingCardCost(emptyCoverage, cardLowerBounds);
            var emptyState = new BeamState(
                SelectedOrderedSellerIndices: [],
                NextSellerIndex: 0,
                CoveredByCard: emptyCoverage,
                CoveredUnits: emptyMetrics.CoveredUnits,
                FullyCoveredCards: emptyMetrics.FullyCoveredCards,
                WeightedPartialCoverage: emptyMetrics.WeightedPartialCoverage,
                SelectedFixedCost: 0m,
                EstimatedRemainingCardCost: emptyRemainingCost,
                Score: ScoreState(
                    emptyMetrics.FullyCoveredCards,
                    emptyMetrics.WeightedPartialCoverage,
                    0m,
                    emptyRemainingCost),
                IsFullCoverage: emptyMetrics.IsFullCoverage);

            var beam = new List<BeamState> { emptyState };
            var bestPartial = emptyState;
            IReadOnlyList<int>? bestFullSelection = null;
            var bestFullCost = InfiniteCost;

            for (var depth = 1; depth <= _orderedSellers.Count; depth++)
            {
                var nextBeam = new List<BeamState>();
                for (var stateIndex = 0; stateIndex < beam.Count; stateIndex++)
                {
                    var state = beam[stateIndex];
                    for (var sellerIndex = state.NextSellerIndex; sellerIndex < _orderedSellers.Count; sellerIndex++)
                    {
                        if (!AddsCoverage(state.CoveredByCard, sellerIndex))
                        {
                            continue;
                        }

                        var expanded = ExpandState(state, sellerIndex, cardLowerBounds);
                        nextBeam.Add(expanded);

                        if (expanded.IsFullCoverage)
                        {
                            var candidateCost = CalculateExactCostForSelection(
                                _orderedSellers,
                                _requiredByCard,
                                _cardCount,
                                expanded.SelectedOrderedSellerIndices);

                            if (IsInfinite(candidateCost))
                            {
                                continue;
                            }

                            if (candidateCost + CostEpsilon < bestFullCost
                                || (Math.Abs(candidateCost - bestFullCost) <= CostEpsilon
                                    && (bestFullSelection is null
                                        || CompareSelectionsLexicographically(
                                            expanded.SelectedOrderedSellerIndices,
                                            bestFullSelection) < 0)))
                            {
                                bestFullCost = candidateCost;
                                bestFullSelection = expanded.SelectedOrderedSellerIndices;
                            }
                        }
                        else if (IsBetterPartial(expanded, bestPartial))
                        {
                            bestPartial = expanded;
                        }
                    }
                }

                if (nextBeam.Count == 0)
                {
                    break;
                }

                nextBeam.Sort(CompareBeamStates);
                beam = nextBeam
                    .Take(_settings.BeamWidth)
                    .ToList();
            }

            if (bestFullSelection is not null)
            {
                return new BeamSearchResult(
                    IsFullCoverage: true,
                    SelectedOrderedSellerIndices: bestFullSelection,
                    TotalCost: bestFullCost);
            }

            return new BeamSearchResult(
                IsFullCoverage: false,
                SelectedOrderedSellerIndices: bestPartial.SelectedOrderedSellerIndices,
                TotalCost: InfiniteCost);
        }

        private bool AddsCoverage(int[] coveredByCard, int sellerIndex)
        {
            var seller = _orderedSellers[sellerIndex];
            foreach (var cardIndex in seller.ActiveCards)
            {
                if (_requiredByCard[cardIndex] <= 0)
                {
                    continue;
                }

                if (coveredByCard[cardIndex] < _requiredByCard[cardIndex] && seller.QtyByCard[cardIndex] > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private BeamState ExpandState(
            BeamState state,
            int sellerIndex,
            IReadOnlyList<decimal[]> cardLowerBounds)
        {
            var seller = _orderedSellers[sellerIndex];
            var selected = new int[state.SelectedOrderedSellerIndices.Count + 1];
            for (var index = 0; index < state.SelectedOrderedSellerIndices.Count; index++)
            {
                selected[index] = state.SelectedOrderedSellerIndices[index];
            }

            selected[selected.Length - 1] = sellerIndex;

            var coveredByCard = (int[])state.CoveredByCard.Clone();
            foreach (var cardIndex in seller.ActiveCards)
            {
                if (_requiredByCard[cardIndex] <= 0)
                {
                    continue;
                }

                coveredByCard[cardIndex] = Math.Min(
                    _requiredByCard[cardIndex],
                    coveredByCard[cardIndex] + seller.QtyByCard[cardIndex]);
            }

            var selectedFixedCost = AddCost(state.SelectedFixedCost, seller.FixedCost);
            var coverageMetrics = BuildCoverageMetrics(coveredByCard);
            var remainingCardCost = EstimateRemainingCardCost(coveredByCard, cardLowerBounds);
            var score = ScoreState(
                coverageMetrics.FullyCoveredCards,
                coverageMetrics.WeightedPartialCoverage,
                selectedFixedCost,
                remainingCardCost);

            return new BeamState(
                SelectedOrderedSellerIndices: selected,
                NextSellerIndex: sellerIndex + 1,
                CoveredByCard: coveredByCard,
                CoveredUnits: coverageMetrics.CoveredUnits,
                FullyCoveredCards: coverageMetrics.FullyCoveredCards,
                WeightedPartialCoverage: coverageMetrics.WeightedPartialCoverage,
                SelectedFixedCost: selectedFixedCost,
                EstimatedRemainingCardCost: remainingCardCost,
                Score: score,
                IsFullCoverage: coverageMetrics.IsFullCoverage);
        }

        private int CompareBeamStates(BeamState left, BeamState right)
        {
            var fullCoverageCompare = right.IsFullCoverage.CompareTo(left.IsFullCoverage);
            if (fullCoverageCompare != 0)
            {
                return fullCoverageCompare;
            }

            var scoreCompare = right.Score.CompareTo(left.Score);
            if (scoreCompare != 0)
            {
                return scoreCompare;
            }

            var coveredCardsCompare = right.FullyCoveredCards.CompareTo(left.FullyCoveredCards);
            if (coveredCardsCompare != 0)
            {
                return coveredCardsCompare;
            }

            var coveredUnitsCompare = right.CoveredUnits.CompareTo(left.CoveredUnits);
            if (coveredUnitsCompare != 0)
            {
                return coveredUnitsCompare;
            }

            var selectedCountCompare = left.SelectedOrderedSellerIndices.Count.CompareTo(right.SelectedOrderedSellerIndices.Count);
            if (selectedCountCompare != 0)
            {
                return selectedCountCompare;
            }

            var fixedCostCompare = left.SelectedFixedCost.CompareTo(right.SelectedFixedCost);
            if (fixedCostCompare != 0)
            {
                return fixedCostCompare;
            }

            return CompareSelectionsLexicographically(
                left.SelectedOrderedSellerIndices,
                right.SelectedOrderedSellerIndices);
        }

        private static bool IsBetterPartial(BeamState candidate, BeamState currentBest)
        {
            if (candidate.FullyCoveredCards != currentBest.FullyCoveredCards)
            {
                return candidate.FullyCoveredCards > currentBest.FullyCoveredCards;
            }

            if (candidate.CoveredUnits != currentBest.CoveredUnits)
            {
                return candidate.CoveredUnits > currentBest.CoveredUnits;
            }

            if (candidate.Score != currentBest.Score)
            {
                return candidate.Score > currentBest.Score;
            }

            return CompareSelectionsLexicographically(
                candidate.SelectedOrderedSellerIndices,
                currentBest.SelectedOrderedSellerIndices) < 0;
        }

        private IReadOnlyList<decimal[]> BuildCardLowerBounds()
        {
            var bounds = new decimal[_cardCount][];

            for (var cardIndex = 0; cardIndex < _cardCount; cardIndex++)
            {
                var requiredQty = _requiredByCard[cardIndex];
                var dp = new decimal[requiredQty + 1];
                Array.Fill(dp, InfiniteCost);
                dp[0] = 0m;

                foreach (var seller in _orderedSellers)
                {
                    var profile = seller.CardProfiles[cardIndex];
                    if (profile is null)
                    {
                        continue;
                    }

                    var next = (decimal[])dp.Clone();
                    var maxTake = Math.Min(requiredQty, profile.QtyUsable);

                    for (var currentQty = 0; currentQty <= requiredQty; currentQty++)
                    {
                        var baseCost = dp[currentQty];
                        if (IsInfinite(baseCost))
                        {
                            continue;
                        }

                        for (var take = 1; take <= maxTake && currentQty + take <= requiredQty; take++)
                        {
                            var newQty = currentQty + take;
                            var candidateCost = AddCost(baseCost, profile.PrefixCosts[take]);
                            if (candidateCost + CostEpsilon < next[newQty])
                            {
                                next[newQty] = candidateCost;
                            }
                        }
                    }

                    dp = next;
                }

                bounds[cardIndex] = dp;
            }

            return bounds;
        }

        private (int FullyCoveredCards, int CoveredUnits, decimal WeightedPartialCoverage, bool IsFullCoverage) BuildCoverageMetrics(
            int[] coveredByCard)
        {
            var fullyCoveredCards = 0;
            var coveredUnits = 0;
            var weightedPartialCoverage = 0m;
            var isFullCoverage = true;

            for (var cardIndex = 0; cardIndex < _cardCount; cardIndex++)
            {
                var requiredQty = _requiredByCard[cardIndex];
                if (requiredQty <= 0)
                {
                    continue;
                }

                var coveredQty = Math.Min(requiredQty, coveredByCard[cardIndex]);
                coveredUnits += coveredQty;

                if (coveredQty >= requiredQty)
                {
                    fullyCoveredCards++;
                    continue;
                }

                isFullCoverage = false;
                weightedPartialCoverage += coveredQty / (decimal)requiredQty;
            }

            return (fullyCoveredCards, coveredUnits, weightedPartialCoverage, isFullCoverage);
        }

        private decimal EstimateRemainingCardCost(
            int[] coveredByCard,
            IReadOnlyList<decimal[]> cardLowerBounds)
        {
            var estimatedRemaining = 0m;

            for (var cardIndex = 0; cardIndex < _cardCount; cardIndex++)
            {
                var requiredQty = _requiredByCard[cardIndex];
                if (requiredQty <= 0)
                {
                    continue;
                }

                var missingQty = Math.Max(0, requiredQty - coveredByCard[cardIndex]);
                if (missingQty == 0)
                {
                    continue;
                }

                var bound = cardLowerBounds[cardIndex][missingQty];
                if (IsInfinite(bound))
                {
                    return InfiniteCost;
                }

                estimatedRemaining = AddCost(estimatedRemaining, bound);
            }

            return estimatedRemaining;
        }

        private decimal ScoreState(
            int fullyCoveredCards,
            decimal weightedPartialCoverage,
            decimal selectedFixedCost,
            decimal estimatedRemainingCardCost)
        {
            if (IsInfinite(estimatedRemainingCardCost))
            {
                return decimal.MinValue / 4m;
            }

            var coverageScore = fullyCoveredCards + (0.25m * weightedPartialCoverage);
            return coverageScore
                - (_settings.BeamAlpha * selectedFixedCost)
                - (_settings.BeamBeta * estimatedRemainingCardCost);
        }

        private sealed record BeamState(
            IReadOnlyList<int> SelectedOrderedSellerIndices,
            int NextSellerIndex,
            int[] CoveredByCard,
            int CoveredUnits,
            int FullyCoveredCards,
            decimal WeightedPartialCoverage,
            decimal SelectedFixedCost,
            decimal EstimatedRemainingCardCost,
            decimal Score,
            bool IsFullCoverage);
    }

    private sealed class ReducedExactSolver
    {
        private readonly IReadOnlyList<CanonicalSeller> _orderedSellers;
        private readonly int[] _requiredByCard;
        private readonly int _cardCount;
        private readonly int _effectiveParallelism;
        private readonly RuntimeSettings _settings;

        public ReducedExactSolver(
            IReadOnlyList<CanonicalSeller> orderedSellers,
            int[] requiredByCard,
            int cardCount,
            int effectiveParallelism,
            RuntimeSettings settings)
        {
            _orderedSellers = orderedSellers;
            _requiredByCard = requiredByCard;
            _cardCount = cardCount;
            _effectiveParallelism = effectiveParallelism;
            _settings = settings;
        }

        public ReducedExactSolveResult Solve(BeamSearchResult incumbent)
        {
            if (_orderedSellers.Count == 0)
            {
                return new ReducedExactSolveResult(false, [], InfiniteCost);
            }

            var sellerCount = _orderedSellers.Count;
            var sellerCardQty = BuildSellerCardQtyMatrix(_orderedSellers, _cardCount);
            var suffixCoverage = BuildSuffixCoverageMatrix(sellerCardQty, sellerCount, _cardCount);
            var suffixMinCosts = BuildSuffixMinimumCosts(_orderedSellers, _requiredByCard, sellerCount, _cardCount);

            var impossibleCardIndices = FindImpossibleCardIndices(_requiredByCard, sellerCardQty, sellerCount);
            if (impossibleCardIndices.Count > 0)
            {
                return new ReducedExactSolveResult(
                    incumbent.IsFullCoverage,
                    incumbent.SelectedOrderedSellerIndices,
                    incumbent.TotalCost);
            }

            var lowerBoundSellerCount = CalculateLowerBoundSellerCount(
                _requiredByCard,
                sellerCardQty,
                _cardCount,
                sellerCount);
            var maxK = Math.Min(_settings.ExactMaxK, sellerCount);

            SelectionResult? bestSelection = null;
            if (incumbent.IsFullCoverage
                && incumbent.SelectedOrderedSellerIndices.Count > 0
                && !IsInfinite(incumbent.TotalCost))
            {
                bestSelection = new SelectionResult(
                    incumbent.SelectedOrderedSellerIndices.ToArray(),
                    incumbent.TotalCost);
            }

            if (lowerBoundSellerCount > maxK)
            {
                return bestSelection is null
                    ? new ReducedExactSolveResult(false, incumbent.SelectedOrderedSellerIndices, incumbent.TotalCost)
                    : new ReducedExactSolveResult(true, bestSelection.SelectedOrderedSellerIndices, bestSelection.TotalCost);
            }

            var deadline = TimeSpan.FromMinutes(_settings.SolverTimeBudgetMinutes);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            for (var targetSellerCount = lowerBoundSellerCount; targetSellerCount <= maxK; targetSellerCount++)
            {
                if (stopwatch.Elapsed >= deadline)
                {
                    break;
                }

                var initialSelection = bestSelection is not null && bestSelection.SelectedOrderedSellerIndices.Count == targetSellerCount
                    ? bestSelection.SelectedOrderedSellerIndices
                    : null;
                var initialUpperBoundCost = bestSelection?.TotalCost ?? InfiniteCost;

                var candidate = FindBestSelectionForFixedK(
                    targetSellerCount,
                    _requiredByCard,
                    _orderedSellers,
                    sellerCardQty,
                    suffixCoverage,
                    suffixMinCosts,
                    sellerCount,
                    _cardCount,
                    _effectiveParallelism,
                    initialUpperBoundCost,
                    initialSelection);

                if (candidate is null || IsInfinite(candidate.TotalCost))
                {
                    continue;
                }

                if (IsBetterCandidate(candidate, bestSelection))
                {
                    bestSelection = candidate;
                }
            }

            if (bestSelection is not null)
            {
                return new ReducedExactSolveResult(
                    IsFullCoverage: true,
                    SelectedOrderedSellerIndices: bestSelection.SelectedOrderedSellerIndices,
                    TotalCost: bestSelection.TotalCost);
            }

            return new ReducedExactSolveResult(
                IsFullCoverage: incumbent.IsFullCoverage,
                SelectedOrderedSellerIndices: incumbent.SelectedOrderedSellerIndices,
                TotalCost: incumbent.TotalCost);
        }
    }

    private sealed class CostSearchState
    {
        private readonly int _targetSellerCount;
        private readonly int[] _requiredByCard;
        private readonly IReadOnlyList<CanonicalSeller> _orderedSellers;
        private readonly int[,] _sellerCardQty;
        private readonly int[,] _suffixCoverage;
        private readonly decimal[,,] _suffixMinCosts;
        private readonly int _sellerCount;
        private readonly int _cardCount;
        private readonly int[] _selectedCoverageByCard;
        private readonly int[] _selectedIndices;
        private readonly decimal[][] _selectedCardDp;
        private readonly decimal[][][] _dpBackupsByDepth;
        private readonly int[][] _touchedCardsByDepth;
        private readonly int[] _touchedCountsByDepth;
        private readonly int _startSellerPosition;
        private readonly int _initialSelectedCount;
        private decimal _bestCost;
        private decimal _selectedFixedCost;
        private IReadOnlyList<int>? _bestSelection;

        public CostSearchState(
            int targetSellerCount,
            int[] requiredByCard,
            IReadOnlyList<CanonicalSeller> orderedSellers,
            int[,] sellerCardQty,
            int[,] suffixCoverage,
            decimal[,,] suffixMinCosts,
            int sellerCount,
            int cardCount,
            IReadOnlyList<int> initialSelectedOrderedSellerIndices,
            int startSellerPosition,
            decimal initialBestCost,
            IReadOnlyList<int>? initialBestSelection)
        {
            _targetSellerCount = targetSellerCount;
            _requiredByCard = requiredByCard;
            _orderedSellers = orderedSellers;
            _sellerCardQty = sellerCardQty;
            _suffixCoverage = suffixCoverage;
            _suffixMinCosts = suffixMinCosts;
            _sellerCount = sellerCount;
            _cardCount = cardCount;
            _selectedCoverageByCard = new int[cardCount];
            _selectedIndices = new int[Math.Max(1, targetSellerCount)];
            _selectedCardDp = new decimal[cardCount][];
            _dpBackupsByDepth = new decimal[Math.Max(2, targetSellerCount + 1)][][];
            _touchedCardsByDepth = new int[Math.Max(2, targetSellerCount + 1)][];
            _touchedCountsByDepth = new int[Math.Max(2, targetSellerCount + 1)];
            _startSellerPosition = Math.Clamp(startSellerPosition, 0, sellerCount);
            _bestCost = initialBestCost;
            _bestSelection = initialBestSelection is null ? null : initialBestSelection.ToArray();

            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                var requiredQty = requiredByCard[cardIndex];
                _selectedCardDp[cardIndex] = new decimal[requiredQty + 1];
                Array.Fill(_selectedCardDp[cardIndex], InfiniteCost);
                _selectedCardDp[cardIndex][0] = 0m;
            }

            for (var depth = 0; depth < _dpBackupsByDepth.Length; depth++)
            {
                _dpBackupsByDepth[depth] = new decimal[cardCount][];
                _touchedCardsByDepth[depth] = new int[cardCount];

                for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
                {
                    _dpBackupsByDepth[depth][cardIndex] = new decimal[_requiredByCard[cardIndex] + 1];
                }
            }

            for (var offset = 0; offset < initialSelectedOrderedSellerIndices.Count; offset++)
            {
                var sellerIndex = initialSelectedOrderedSellerIndices[offset];
                _selectedIndices[offset] = sellerIndex;
                AddCoverage(sellerIndex);
                ApplySellerDpForInitialization(sellerIndex);
            }

            _initialSelectedCount = initialSelectedOrderedSellerIndices.Count;
        }

        public SelectionResult? BestResult
            => _bestSelection is null || IsInfinite(_bestCost)
                ? null
                : new SelectionResult(_bestSelection, _bestCost);

        public void Search()
        {
            SearchRecursive(_startSellerPosition, _initialSelectedCount);
        }

        private void SearchRecursive(int sellerPosition, int selectedCount)
        {
            if (selectedCount > _targetSellerCount)
            {
                return;
            }

            if (selectedCount + (_sellerCount - sellerPosition) < _targetSellerCount)
            {
                return;
            }

            if (!CanStillCoverAllCards(sellerPosition))
            {
                return;
            }

            if (selectedCount == _targetSellerCount)
            {
                if (IsFullyCovered())
                {
                    var candidateCost = CalculateCurrentSelectionCost();
                    if (!IsInfinite(candidateCost) && IsBetterCandidate(candidateCost))
                    {
                        _bestCost = candidateCost;
                        _bestSelection = _selectedIndices.Take(_targetSellerCount).ToArray();
                    }
                }

                return;
            }

            if (sellerPosition >= _sellerCount)
            {
                return;
            }

            var lowerBound = CalculateLowerBoundCost(sellerPosition);
            if (!IsInfinite(_bestCost) && lowerBound >= _bestCost - CostEpsilon)
            {
                return;
            }

            _selectedIndices[selectedCount] = sellerPosition;
            var depth = selectedCount + 1;
            AddCoverage(sellerPosition);
            ApplySellerDp(sellerPosition, depth);
            SearchRecursive(sellerPosition + 1, selectedCount + 1);
            RestoreSellerDp(depth);
            RemoveCoverage(sellerPosition);

            SearchRecursive(sellerPosition + 1, selectedCount);
        }

        private void ApplySellerDpForInitialization(int sellerIndex)
        {
            var seller = _orderedSellers[sellerIndex];

            foreach (var cardIndex in seller.ActiveCards)
            {
                var profile = seller.CardProfiles[cardIndex]!;
                var requiredQty = _requiredByCard[cardIndex];
                var current = _selectedCardDp[cardIndex];
                var next = (decimal[])current.Clone();
                var maxTake = Math.Min(profile.QtyUsable, requiredQty);

                for (var currentQty = 0; currentQty <= requiredQty; currentQty++)
                {
                    var baseCost = current[currentQty];
                    if (IsInfinite(baseCost))
                    {
                        continue;
                    }

                    for (var take = 1; take <= maxTake && currentQty + take <= requiredQty; take++)
                    {
                        var newQty = currentQty + take;
                        var candidateCost = AddCost(baseCost, profile.PrefixCosts[take]);
                        if (candidateCost + CostEpsilon < next[newQty])
                        {
                            next[newQty] = candidateCost;
                        }
                    }
                }

                _selectedCardDp[cardIndex] = next;
            }
        }

        private void ApplySellerDp(int sellerIndex, int depth)
        {
            var seller = _orderedSellers[sellerIndex];
            _touchedCountsByDepth[depth] = 0;

            foreach (var cardIndex in seller.ActiveCards)
            {
                var requiredQty = _requiredByCard[cardIndex];
                var profile = seller.CardProfiles[cardIndex]!;
                var row = _selectedCardDp[cardIndex];
                var backup = _dpBackupsByDepth[depth][cardIndex];
                Array.Copy(row, backup, row.Length);

                var maxTake = Math.Min(profile.QtyUsable, requiredQty);
                row[0] = backup[0];
                for (var qty = 1; qty <= requiredQty; qty++)
                {
                    row[qty] = InfiniteCost;
                }

                for (var currentQty = 0; currentQty <= requiredQty; currentQty++)
                {
                    var baseCost = backup[currentQty];
                    if (IsInfinite(baseCost))
                    {
                        continue;
                    }

                    if (baseCost + CostEpsilon < row[currentQty])
                    {
                        row[currentQty] = baseCost;
                    }

                    for (var take = 1; take <= maxTake && currentQty + take <= requiredQty; take++)
                    {
                        var newQty = currentQty + take;
                        var candidateCost = AddCost(baseCost, profile.PrefixCosts[take]);
                        if (candidateCost + CostEpsilon < row[newQty])
                        {
                            row[newQty] = candidateCost;
                        }
                    }
                }

                _touchedCardsByDepth[depth][_touchedCountsByDepth[depth]++] = cardIndex;
            }
        }

        private void RestoreSellerDp(int depth)
        {
            for (var touchedOffset = 0; touchedOffset < _touchedCountsByDepth[depth]; touchedOffset++)
            {
                var cardIndex = _touchedCardsByDepth[depth][touchedOffset];
                var row = _selectedCardDp[cardIndex];
                var backup = _dpBackupsByDepth[depth][cardIndex];
                Array.Copy(backup, row, row.Length);
            }

            _touchedCountsByDepth[depth] = 0;
        }

        private void AddCoverage(int sellerIndex)
        {
            foreach (var cardIndex in _orderedSellers[sellerIndex].ActiveCards)
            {
                _selectedCoverageByCard[cardIndex] += _sellerCardQty[sellerIndex, cardIndex];
            }

            _selectedFixedCost = AddCost(_selectedFixedCost, _orderedSellers[sellerIndex].FixedCost);
        }

        private void RemoveCoverage(int sellerIndex)
        {
            foreach (var cardIndex in _orderedSellers[sellerIndex].ActiveCards)
            {
                _selectedCoverageByCard[cardIndex] -= _sellerCardQty[sellerIndex, cardIndex];
            }

            _selectedFixedCost -= _orderedSellers[sellerIndex].FixedCost;
        }

        private bool CanStillCoverAllCards(int sellerPosition)
        {
            for (var cardIndex = 0; cardIndex < _cardCount; cardIndex++)
            {
                var maxPossible = _selectedCoverageByCard[cardIndex] + _suffixCoverage[sellerPosition, cardIndex];
                if (maxPossible < _requiredByCard[cardIndex])
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsFullyCovered()
        {
            for (var cardIndex = 0; cardIndex < _cardCount; cardIndex++)
            {
                if (_selectedCoverageByCard[cardIndex] < _requiredByCard[cardIndex])
                {
                    return false;
                }
            }

            return true;
        }

        private decimal CalculateCurrentSelectionCost()
        {
            var totalCost = 0m;

            for (var cardIndex = 0; cardIndex < _cardCount; cardIndex++)
            {
                var requiredQty = _requiredByCard[cardIndex];
                var cardCost = _selectedCardDp[cardIndex][requiredQty];
                if (IsInfinite(cardCost))
                {
                    return InfiniteCost;
                }

                totalCost = AddCost(totalCost, cardCost);
            }

            return AddCost(totalCost, _selectedFixedCost);
        }

        private decimal CalculateLowerBoundCost(int sellerPosition)
        {
            var totalLowerBound = 0m;

            for (var cardIndex = 0; cardIndex < _cardCount; cardIndex++)
            {
                var requiredQty = _requiredByCard[cardIndex];
                var row = _selectedCardDp[cardIndex];
                var bestCardBound = InfiniteCost;

                for (var selectedQty = 0; selectedQty <= requiredQty; selectedQty++)
                {
                    var selectedCost = row[selectedQty];
                    if (IsInfinite(selectedCost))
                    {
                        continue;
                    }

                    var missingQty = requiredQty - selectedQty;
                    var suffixCost = _suffixMinCosts[sellerPosition, cardIndex, missingQty];
                    if (IsInfinite(suffixCost))
                    {
                        continue;
                    }

                    var candidateBound = AddCost(selectedCost, suffixCost);
                    if (candidateBound + CostEpsilon < bestCardBound)
                    {
                        bestCardBound = candidateBound;
                    }
                }

                if (IsInfinite(bestCardBound))
                {
                    return InfiniteCost;
                }

                totalLowerBound = AddCost(totalLowerBound, bestCardBound);
            }

            return AddCost(totalLowerBound, _selectedFixedCost);
        }

        private bool IsBetterCandidate(decimal candidateCost)
        {
            if (IsInfinite(_bestCost))
            {
                return true;
            }

            if (candidateCost + CostEpsilon < _bestCost)
            {
                return true;
            }

            if (_bestCost + CostEpsilon < candidateCost)
            {
                return false;
            }

            if (_bestSelection is null)
            {
                return true;
            }

            return CompareSelectionsLexicographically(_selectedIndices, _targetSellerCount, _bestSelection) < 0;
        }
    }

    private sealed record RuntimeSettings(
        int CandidateTopCheapestPerCard,
        int CandidateTopEffectivePerCard,
        int CandidatePoolMin,
        int CandidatePoolMax,
        int BeamWidth,
        decimal BeamAlpha,
        decimal BeamBeta,
        int ExactMaxK,
        bool EnableFinalCostRefine,
        int SolverTimeBudgetMinutes);

    private sealed record BeamSearchResult(
        bool IsFullCoverage,
        IReadOnlyList<int> SelectedOrderedSellerIndices,
        decimal TotalCost);

    private sealed record ReducedExactSolveResult(
        bool IsFullCoverage,
        IReadOnlyList<int> SelectedOrderedSellerIndices,
        decimal TotalCost);

    private sealed record SelectionResult(
        IReadOnlyList<int> SelectedOrderedSellerIndices,
        decimal TotalCost);

    private sealed record SelectionCoverageQuality(
        IReadOnlyList<int> SelectedOrderedSellerIndices,
        int[] CoveredByCard,
        int FullyCoveredCards,
        int CoveredUnits,
        decimal CoveredCost,
        bool IsFullyCovered);

    private sealed record PartialSelectionObjective(
        IReadOnlyList<int> SelectedOrderedSellerIndices,
        IReadOnlyList<int> UncoveredCardIndices,
        decimal TotalCost);

    private sealed class SellerCardProfile
    {
        public SellerCardProfile(decimal[] prefixCosts)
        {
            PrefixCosts = prefixCosts;
        }

        public decimal[] PrefixCosts { get; }

        public int QtyUsable => PrefixCosts.Length - 1;
    }

    private sealed class CanonicalSeller
    {
        public CanonicalSeller(
            int originalOrder,
            string sellerName,
            decimal fixedCost,
            SellerCardProfile?[] cardProfiles,
            int[] qtyByCard,
            int[] activeCards)
        {
            OriginalOrder = originalOrder;
            SellerName = sellerName;
            FixedCost = fixedCost;
            CardProfiles = cardProfiles;
            QtyByCard = qtyByCard;
            ActiveCards = activeCards;
        }

        public int OriginalOrder { get; }

        public string SellerName { get; }

        public decimal FixedCost { get; }

        public SellerCardProfile?[] CardProfiles { get; }

        public int[] QtyByCard { get; }

        public int[] ActiveCards { get; }

        public int ActiveCardCount => ActiveCards.Length;
    }

    private sealed class SellerAccumulator
    {
        private readonly List<SellerOffer>?[] _offersByCard;

        public SellerAccumulator(int originalOrder, string sellerName, int cardCount)
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
}
