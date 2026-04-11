using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;

using System.Diagnostics;

using CMBuyerStudio.Application.Optimization;

namespace CMBuyerStudio.Application.Services;

public sealed class OfferPurger
{
    private const decimal CostEpsilon = 0.000001m;

    public OfferPurgeResult Purge(
    IReadOnlyList<MarketCardData> marketData,
    IReadOnlyDictionary<string, decimal> fixedCostBySellerName)
    {
        ArgumentNullException.ThrowIfNull(marketData);
        ArgumentNullException.ThrowIfNull(fixedCostBySellerName);

        if (marketData.Count == 0)
        {
            return new OfferPurgeResult
            {
                ScopedMarketData = [],
                PurgedMarketData = [],
                RemainingRequiredByCardKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                PreselectedSellerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                UncoveredCardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Stats = new OfferPurgeStats(),
                ProfilePhases = []
            };
        }

        var totalStopwatch = Stopwatch.StartNew();
        var pipeline = RunPurgePipeline(marketData, fixedCostBySellerName);
        totalStopwatch.Stop();

        var purgedMarketData = RebuildPurgedMarketData(
            marketData,
            pipeline.RemainingSellers);

        var uncoveredCardKeys = pipeline.UncoveredCardIndices
            .Select(index => ResolveCardKey(marketData[index].Target))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var remainingRequiredByCardKey = Enumerable.Range(0, marketData.Count)
            .ToDictionary(
                index => ResolveCardKey(marketData[index].Target),
                index => Math.Max(0, pipeline.RequiredByCard[index]),
                StringComparer.OrdinalIgnoreCase);

        return new OfferPurgeResult
        {
            ScopedMarketData = marketData,
            PurgedMarketData = purgedMarketData,
            RemainingRequiredByCardKey = remainingRequiredByCardKey,
            PreselectedSellerNames = pipeline.PreselectedSellerNames,
            UncoveredCardKeys = uncoveredCardKeys,
            Stats = new OfferPurgeStats
            {
                InitialSellerCount = pipeline.InitialSellerCount,
                RemainingSellerCount = pipeline.RemainingSellers.Count,
                RemovedUseless = pipeline.TotalRemovedUseless,
                RemovedSingleCardDominated = pipeline.TotalRemovedSingleCardDominated,
                RemovedGlobalDominated = pipeline.TotalRemovedGlobalDominated
            },
            ProfilePhases =
            [
                .. pipeline.ProfilePhases,
                new OptimizationPhaseProfile
                {
                    Name = "Purge.Total",
                    ElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds,
                    Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["initialSellerCount"] = pipeline.InitialSellerCount,
                        ["remainingSellerCount"] = pipeline.RemainingSellers.Count,
                        ["rounds"] = pipeline.Rounds,
                        ["uncoveredCards"] = pipeline.UncoveredCardIndices.Count
                    }
                }
            ]
        };
    }

    private static string ResolveCardKey(ScrapingTarget target)
        => string.IsNullOrWhiteSpace(target.RequestKey)
            ? target.ProductUrl
            : target.RequestKey;

    private static CanonicalSellerBuildResult<CanonicalSeller> BuildCanonicalSellersWithMetrics(
    IReadOnlyList<MarketCardData> marketData,
    int[] requiredByCard,
    IReadOnlyDictionary<string, decimal> fixedCostBySellerName)
    {
        return CanonicalSellerBuildHelper.BuildCanonicalSellers(
            marketData,
            requiredByCard,
            fixedCostBySellerName,
            (originalOrder, sellerName, fixedCost, profileData, qtyByCard, activeCards) => new CanonicalSeller(
                originalOrder,
                sellerName,
                fixedCost,
                profileData.Select(profile => profile is null ? null : new SellerCardProfile(profile.PrefixCosts)).ToArray(),
                qtyByCard,
                activeCards),
            ResolveParallelism());
    }

    private static List<int> FindImpossibleCardIndices(
    IReadOnlyList<CanonicalSeller> sellers,
    int[] requiredByCard)
    {
        var impossibleCardIndices = new List<int>();

        for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
        {
            if (requiredByCard[cardIndex] <= 0)
            {
                continue;
            }

            var totalQty = 0;
            for (var sellerIndex = 0; sellerIndex < sellers.Count; sellerIndex++)
            {
                totalQty += sellers[sellerIndex].QtyByCard[cardIndex];
            }

            if (totalQty < requiredByCard[cardIndex])
            {
                impossibleCardIndices.Add(cardIndex);
            }
        }

        return impossibleCardIndices;
    }

    private static bool DominatesSingleCard(CanonicalSeller dominant, CanonicalSeller target, int cardIndex)
    {
        var targetQty = target.QtyByCard[cardIndex];
        var dominantQty = dominant.QtyByCard[cardIndex];

        if (targetQty <= 0 || dominantQty < targetQty)
        {
            return false;
        }

        var dominantProfile = dominant.CardProfiles[cardIndex];
        var targetProfile = target.CardProfiles[cardIndex];

        if (dominantProfile is null || targetProfile is null)
        {
            return false;
        }

        var hasStrictImprovement = dominantQty > targetQty;

        for (var qty = 1; qty <= targetQty; qty++)
        {
            var dominantCost = dominantProfile.PrefixCosts[qty] + dominant.FixedCost;
            var targetCost = targetProfile.PrefixCosts[qty] + target.FixedCost;

            if (dominantCost > targetCost + CostEpsilon)
            {
                return false;
            }

            if (dominantCost + CostEpsilon < targetCost)
            {
                hasStrictImprovement = true;
            }
        }

        return hasStrictImprovement;
    }

    private static bool DominatesGlobally(CanonicalSeller dominant, CanonicalSeller target)
    {
        if (dominant.ActiveCardCount < target.ActiveCardCount)
        {
            return false;
        }

        var hasStrictImprovement = dominant.ActiveCardCount > target.ActiveCardCount;

        if (dominant.FixedCost > target.FixedCost + CostEpsilon)
        {
            return false;
        }

        if (dominant.FixedCost + CostEpsilon < target.FixedCost)
        {
            hasStrictImprovement = true;
        }

        foreach (var cardIndex in target.ActiveCards)
        {
            var targetQty = target.QtyByCard[cardIndex];
            var dominantQty = dominant.QtyByCard[cardIndex];

            if (dominantQty < targetQty)
            {
                return false;
            }

            if (dominantQty > targetQty)
            {
                hasStrictImprovement = true;
            }

            var dominantProfile = dominant.CardProfiles[cardIndex];
            var targetProfile = target.CardProfiles[cardIndex];

            if (dominantProfile is null || targetProfile is null)
            {
                return false;
            }

            for (var qty = 1; qty <= targetQty; qty++)
            {
                var dominantCost = dominantProfile.PrefixCosts[qty];
                var targetCost = targetProfile.PrefixCosts[qty];

                if (dominantCost > targetCost + CostEpsilon)
                {
                    return false;
                }

                if (dominantCost + CostEpsilon < targetCost)
                {
                    hasStrictImprovement = true;
                }
            }
        }

        return hasStrictImprovement;
    }

    private static int RemoveDominatedSingleCardSellers(
    IReadOnlyList<CanonicalSeller> sellers,
    bool[] removed,
    int[] totalQtyByCard,
    int[] requiredByCard,
    int cardCount)
    {
        var removedCount = 0;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            if (requiredByCard[cardIndex] <= 0)
            {
                continue;
            }

            var candidates = Enumerable.Range(0, sellers.Count)
                .Where(index =>
                    !removed[index] &&
                    sellers[index].ActiveCardCount == 1 &&
                    sellers[index].QtyByCard[cardIndex] > 0)
                .ToList();

            if (candidates.Count <= 1)
            {
                continue;
            }

            for (var targetOffset = 0; targetOffset < candidates.Count; targetOffset++)
            {
                var targetIndex = candidates[targetOffset];
                if (removed[targetIndex])
                {
                    continue;
                }

                for (var dominantOffset = 0; dominantOffset < candidates.Count; dominantOffset++)
                {
                    if (targetOffset == dominantOffset)
                    {
                        continue;
                    }

                    var dominantIndex = candidates[dominantOffset];
                    if (removed[dominantIndex])
                    {
                        continue;
                    }

                    if (!DominatesSingleCard(sellers[dominantIndex], sellers[targetIndex], cardIndex))
                    {
                        continue;
                    }

                    if (totalQtyByCard[cardIndex] - sellers[targetIndex].QtyByCard[cardIndex] < requiredByCard[cardIndex])
                    {
                        continue;
                    }

                    removed[targetIndex] = true;
                    totalQtyByCard[cardIndex] -= sellers[targetIndex].QtyByCard[cardIndex];
                    removedCount++;
                    break;
                }
            }
        }

        return removedCount;
    }

    private static PreprocessResult PreprocessSellersExact(
    IReadOnlyList<CanonicalSeller> sellers,
    int cardCount,
    int[] requiredByCard)
    {
        var removed = new bool[sellers.Count];
        var totalQtyByCard = new int[cardCount];

        for (var sellerIndex = 0; sellerIndex < sellers.Count; sellerIndex++)
        {
            foreach (var cardIndex in sellers[sellerIndex].ActiveCards)
            {
                if (requiredByCard[cardIndex] <= 0)
                {
                    continue;
                }

                totalQtyByCard[cardIndex] += sellers[sellerIndex].QtyByCard[cardIndex];
            }
        }

        var removedUseless = 0;
        for (var sellerIndex = 0; sellerIndex < sellers.Count; sellerIndex++)
        {
            if (sellers[sellerIndex].ActiveCardCount > 0)
            {
                continue;
            }

            removed[sellerIndex] = true;
            removedUseless++;
        }

        var removedSingleCardDominated = RemoveDominatedSingleCardSellers(
            sellers,
            removed,
            totalQtyByCard,
            requiredByCard,
            cardCount);

        var candidateIndices = Enumerable.Range(0, sellers.Count)
            .Where(index => !removed[index])
            .ToArray();

        var removedGlobalDominated = 0;

        for (var targetOffset = 0; targetOffset < candidateIndices.Length; targetOffset++)
        {
            var targetIndex = candidateIndices[targetOffset];
            if (removed[targetIndex])
            {
                continue;
            }

            for (var candidateOffset = 0; candidateOffset < candidateIndices.Length; candidateOffset++)
            {
                if (targetOffset == candidateOffset)
                {
                    continue;
                }

                var dominantIndex = candidateIndices[candidateOffset];
                if (removed[dominantIndex])
                {
                    continue;
                }

                if (!DominatesGlobally(sellers[dominantIndex], sellers[targetIndex]))
                {
                    continue;
                }

                var canRemove = true;

                foreach (var cardIndex in sellers[targetIndex].ActiveCards)
                {
                    if (requiredByCard[cardIndex] <= 0)
                    {
                        continue;
                    }

                    if (totalQtyByCard[cardIndex] - sellers[targetIndex].QtyByCard[cardIndex] < requiredByCard[cardIndex])
                    {
                        canRemove = false;
                        break;
                    }
                }

                if (!canRemove)
                {
                    continue;
                }

                removed[targetIndex] = true;
                removedGlobalDominated++;

                foreach (var cardIndex in sellers[targetIndex].ActiveCards)
                {
                    if (requiredByCard[cardIndex] <= 0)
                    {
                        continue;
                    }

                    totalQtyByCard[cardIndex] -= sellers[targetIndex].QtyByCard[cardIndex];
                }

                break;
            }
        }

        var reduced = Enumerable.Range(0, sellers.Count)
            .Where(index => !removed[index])
            .Select(index => sellers[index])
            .ToList();

        return new PreprocessResult(
            reduced,
            removedUseless,
            removedSingleCardDominated,
            removedGlobalDominated);
    }

    private static IReadOnlyList<MarketCardData> RebuildPurgedMarketData(
    IReadOnlyList<MarketCardData> originalMarketData,
    IReadOnlyCollection<CanonicalSeller> remainingSellers)
    {
        var remainingSellerNames = remainingSellers
            .Select(x => x.SellerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return originalMarketData
            .Select(card => new MarketCardData
            {
                Target = card.Target,
                ScrapedAtUtc = card.ScrapedAtUtc,
                Offers = card.Offers
                    .Where(offer => remainingSellerNames.Contains(offer.SellerName))
                    .ToList()
            })
            .ToList();
    }












    private static int CountActiveCards(int[] requiredByCard)
    => requiredByCard.Count(x => x > 0);

    private static HashSet<string> FindForcedSellerNames(
    IReadOnlyList<CanonicalSeller> sellers,
    int[] requiredByCard)
    {
        var forcedSellerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sellersByCard = BuildSellersByCard(sellers, requiredByCard.Length);

        for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
        {
            var requiredQty = requiredByCard[cardIndex];
            if (requiredQty <= 0)
            {
                continue;
            }

            var cardSellers = sellersByCard[cardIndex];
            if (cardSellers.Count == 0)
            {
                continue;
            }

            var totalQty = 0;
            for (var offset = 0; offset < cardSellers.Count; offset++)
            {
                totalQty += sellers[cardSellers[offset]].QtyByCard[cardIndex];
            }

            for (var offset = 0; offset < cardSellers.Count; offset++)
            {
                var sellerIndex = cardSellers[offset];
                var sellerQty = sellers[sellerIndex].QtyByCard[cardIndex];

                if (totalQty - sellerQty < requiredQty)
                {
                    forcedSellerNames.Add(sellers[sellerIndex].SellerName);
                }
            }
        }

        return forcedSellerNames;
    }

    private static List<int>[] BuildSellersByCard(
    IReadOnlyList<CanonicalSeller> sellers,
    int cardCount)
    {
        var sellersByCard = new List<int>[cardCount];

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            sellersByCard[cardIndex] = [];
        }

        for (var sellerIndex = 0; sellerIndex < sellers.Count; sellerIndex++)
        {
            foreach (var cardIndex in sellers[sellerIndex].ActiveCards)
            {
                sellersByCard[cardIndex].Add(sellerIndex);
            }
        }

        return sellersByCard;
    }

    private static void ApplySelectedSellersCapacity(
    IReadOnlyList<CanonicalSeller> sellers,
    IReadOnlyCollection<string> selectedSellerNames,
    int[] requiredByCard)
    {
        if (selectedSellerNames.Count == 0)
        {
            return;
        }

        for (var sellerIndex = 0; sellerIndex < sellers.Count; sellerIndex++)
        {
            var seller = sellers[sellerIndex];
            if (!selectedSellerNames.Contains(seller.SellerName))
            {
                continue;
            }

            foreach (var cardIndex in seller.ActiveCards)
            {
                if (requiredByCard[cardIndex] <= 0)
                {
                    continue;
                }

                requiredByCard[cardIndex] = Math.Max(
                    0,
                    requiredByCard[cardIndex] - seller.QtyByCard[cardIndex]);
            }
        }
    }

    private static PurgePipelineResult RunPurgePipeline(
    IReadOnlyList<MarketCardData> marketData,
    IReadOnlyDictionary<string, decimal> fixedCostBySellerName)
    {
        var profilePhases = new List<OptimizationPhaseProfile>();
        var requiredByCard = marketData
            .Select(x => Math.Max(0, x.Target.DesiredQuantity))
            .ToArray();

        var preselectedSellerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uncoveredCardIndices = new HashSet<int>();

        var initialBuildStopwatch = Stopwatch.StartNew();
        var initialBuild = BuildCanonicalSellersWithMetrics(
            marketData,
            requiredByCard,
            fixedCostBySellerName);
        initialBuildStopwatch.Stop();
        IReadOnlyList<CanonicalSeller> currentSellers = initialBuild.Sellers;
        profilePhases.Add(CreateCanonicalBuildPhase(
            "Purge.BuildCanonicalSellers.Initial",
            initialBuildStopwatch.ElapsedMilliseconds,
            initialBuild.Metrics));

        foreach (var impossibleCardIndex in FindImpossibleCardIndices(currentSellers, requiredByCard))
        {
            uncoveredCardIndices.Add(impossibleCardIndex);
            requiredByCard[impossibleCardIndex] = 0;
        }

        if (uncoveredCardIndices.Count > 0)
        {
            var rebuildStopwatch = Stopwatch.StartNew();
            var rebuilt = BuildCanonicalSellersWithMetrics(
                marketData,
                requiredByCard,
                fixedCostBySellerName);
            rebuildStopwatch.Stop();
            currentSellers = rebuilt.Sellers;
            profilePhases.Add(CreateCanonicalBuildPhase(
                "Purge.BuildCanonicalSellers.PostImpossible",
                rebuildStopwatch.ElapsedMilliseconds,
                rebuilt.Metrics));
        }

        var initialSellerCount = currentSellers.Count;

        var totalRemovedUseless = 0;
        var totalRemovedSingleCardDominated = 0;
        var totalRemovedGlobalDominated = 0;
        var round = 0;

        while (true)
        {
            round++;

            var sellersBefore = currentSellers.Count;
            var cardsBefore = CountActiveCards(requiredByCard);

            var preprocessStopwatch = Stopwatch.StartNew();
            var preprocess = PreprocessSellersExact(
                currentSellers,
                marketData.Count,
                requiredByCard);
            preprocessStopwatch.Stop();
            profilePhases.Add(new OptimizationPhaseProfile
            {
                Name = $"Purge.Preprocess.Round{round}",
                ElapsedMilliseconds = preprocessStopwatch.ElapsedMilliseconds,
                Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["inputSellers"] = sellersBefore,
                    ["remainingSellers"] = preprocess.Sellers.Count,
                    ["removedUseless"] = preprocess.RemovedUseless,
                    ["removedSingleCardDominated"] = preprocess.RemovedSingleCardDominated,
                    ["removedGlobalDominated"] = preprocess.RemovedGlobalDominated
                }
            });

            currentSellers = preprocess.Sellers;

            totalRemovedUseless += preprocess.RemovedUseless;
            totalRemovedSingleCardDominated += preprocess.RemovedSingleCardDominated;
            totalRemovedGlobalDominated += preprocess.RemovedGlobalDominated;

            var isolatedStopwatch = Stopwatch.StartNew();
            var isolatedResult = SolveIsolatedSingleCardCards(currentSellers, requiredByCard);
            isolatedStopwatch.Stop();
            profilePhases.Add(new OptimizationPhaseProfile
            {
                Name = $"Purge.Isolated.Round{round}",
                ElapsedMilliseconds = isolatedStopwatch.ElapsedMilliseconds,
                Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["solutions"] = isolatedResult.Solutions.Count,
                    ["impossibleCards"] = isolatedResult.ImpossibleCardIndices.Count
                }
            });

            foreach (var uncoveredCardIndex in isolatedResult.ImpossibleCardIndices)
            {
                uncoveredCardIndices.Add(uncoveredCardIndex);
            }

            foreach (var isolatedSolution in isolatedResult.Solutions)
            {
                requiredByCard[isolatedSolution.CardIndex] = 0;
                preselectedSellerNames.UnionWith(isolatedSolution.SelectedSellerNames);
            }

            if (isolatedResult.Solutions.Count > 0)
            {
                var isolatedSellerNames = isolatedResult.Solutions
                    .SelectMany(solution => solution.SelectedSellerNames)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                ApplySelectedSellersCapacity(
                    currentSellers,
                    isolatedSellerNames,
                    requiredByCard);

                var postIsolatedBuildStopwatch = Stopwatch.StartNew();
                var postIsolatedBuild = BuildCanonicalSellersWithMetrics(
                    marketData,
                    requiredByCard,
                    fixedCostBySellerName);
                postIsolatedBuildStopwatch.Stop();
                currentSellers = postIsolatedBuild.Sellers;
                profilePhases.Add(CreateCanonicalBuildPhase(
                    $"Purge.BuildCanonicalSellers.PostIsolated.Round{round}",
                    postIsolatedBuildStopwatch.ElapsedMilliseconds,
                    postIsolatedBuild.Metrics));

                var postIsolatedPreprocessStopwatch = Stopwatch.StartNew();
                var postIsolatedPreprocess = PreprocessSellersExact(
                    currentSellers,
                    marketData.Count,
                    requiredByCard);
                postIsolatedPreprocessStopwatch.Stop();
                profilePhases.Add(new OptimizationPhaseProfile
                {
                    Name = $"Purge.PreprocessPostIsolated.Round{round}",
                    ElapsedMilliseconds = postIsolatedPreprocessStopwatch.ElapsedMilliseconds,
                    Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["remainingSellers"] = postIsolatedPreprocess.Sellers.Count,
                        ["removedUseless"] = postIsolatedPreprocess.RemovedUseless,
                        ["removedSingleCardDominated"] = postIsolatedPreprocess.RemovedSingleCardDominated,
                        ["removedGlobalDominated"] = postIsolatedPreprocess.RemovedGlobalDominated
                    }
                });

                currentSellers = postIsolatedPreprocess.Sellers;

                totalRemovedUseless += postIsolatedPreprocess.RemovedUseless;
                totalRemovedSingleCardDominated += postIsolatedPreprocess.RemovedSingleCardDominated;
                totalRemovedGlobalDominated += postIsolatedPreprocess.RemovedGlobalDominated;
            }

            var forcedStopwatch = Stopwatch.StartNew();
            var forcedSellerNames = FindForcedSellerNames(currentSellers, requiredByCard);
            forcedStopwatch.Stop();
            var newForcedSellerNames = forcedSellerNames
                .Where(x => !preselectedSellerNames.Contains(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            profilePhases.Add(new OptimizationPhaseProfile
            {
                Name = $"Purge.ForcedSellers.Round{round}",
                ElapsedMilliseconds = forcedStopwatch.ElapsedMilliseconds,
                Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["forcedSellers"] = forcedSellerNames.Count,
                    ["newForcedSellers"] = newForcedSellerNames.Count
                }
            });

            if (newForcedSellerNames.Count > 0)
            {
                preselectedSellerNames.UnionWith(newForcedSellerNames);

                ApplySelectedSellersCapacity(
                    currentSellers,
                    newForcedSellerNames,
                    requiredByCard);

                var postForcedBuildStopwatch = Stopwatch.StartNew();
                var postForcedBuild = BuildCanonicalSellersWithMetrics(
                    marketData,
                    requiredByCard,
                    fixedCostBySellerName);
                postForcedBuildStopwatch.Stop();
                currentSellers = postForcedBuild.Sellers;
                profilePhases.Add(CreateCanonicalBuildPhase(
                    $"Purge.BuildCanonicalSellers.PostForced.Round{round}",
                    postForcedBuildStopwatch.ElapsedMilliseconds,
                    postForcedBuild.Metrics));

                var postForcedPreprocessStopwatch = Stopwatch.StartNew();
                var postForcedPreprocess = PreprocessSellersExact(
                    currentSellers,
                    marketData.Count,
                    requiredByCard);
                postForcedPreprocessStopwatch.Stop();
                profilePhases.Add(new OptimizationPhaseProfile
                {
                    Name = $"Purge.PreprocessPostForced.Round{round}",
                    ElapsedMilliseconds = postForcedPreprocessStopwatch.ElapsedMilliseconds,
                    Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["remainingSellers"] = postForcedPreprocess.Sellers.Count,
                        ["removedUseless"] = postForcedPreprocess.RemovedUseless,
                        ["removedSingleCardDominated"] = postForcedPreprocess.RemovedSingleCardDominated,
                        ["removedGlobalDominated"] = postForcedPreprocess.RemovedGlobalDominated
                    }
                });

                currentSellers = postForcedPreprocess.Sellers;

                totalRemovedUseless += postForcedPreprocess.RemovedUseless;
                totalRemovedSingleCardDominated += postForcedPreprocess.RemovedSingleCardDominated;
                totalRemovedGlobalDominated += postForcedPreprocess.RemovedGlobalDominated;
            }

            var sellersAfter = currentSellers.Count;
            var cardsAfter = CountActiveCards(requiredByCard);

            var changed =
                sellersAfter < sellersBefore ||
                cardsAfter < cardsBefore ||
                preprocess.RemovedUseless > 0 ||
                preprocess.RemovedSingleCardDominated > 0 ||
                preprocess.RemovedGlobalDominated > 0 ||
                isolatedResult.Solutions.Count > 0 ||
                newForcedSellerNames.Count > 0;

            if (!changed)
            {
                return new PurgePipelineResult(
                    RemainingSellers: currentSellers,
                    RequiredByCard: requiredByCard,
                    PreselectedSellerNames: preselectedSellerNames,
                    UncoveredCardIndices: uncoveredCardIndices,
                    ProfilePhases: profilePhases,
                    TotalRemovedUseless: totalRemovedUseless,
                    TotalRemovedSingleCardDominated: totalRemovedSingleCardDominated,
                    TotalRemovedGlobalDominated: totalRemovedGlobalDominated,
                    InitialSellerCount: initialSellerCount,
                    Rounds: round);
            }
        }
    }

    private static OptimizationPhaseProfile CreateCanonicalBuildPhase(
        string name,
        long elapsedMilliseconds,
        CanonicalSellerBuildMetrics metrics)
    {
        return new OptimizationPhaseProfile
        {
            Name = name,
            ElapsedMilliseconds = elapsedMilliseconds,
            Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["cardCount"] = metrics.CardCount,
                ["offerCount"] = metrics.OfferCount,
                ["uniqueSellerCount"] = metrics.UniqueSellerCount,
                ["activeSellerCount"] = metrics.ActiveSellerCount,
                ["activeProfileCount"] = metrics.ActiveProfileCount
            }
        };
    }

    private static int ResolveParallelism()
        => Math.Max(1, Environment.ProcessorCount / 2);

    private static void UpsertIsolatedDpState(
    Dictionary<(int Selected, int Qty), IsolatedDpState> layer,
    int selected,
    int qty,
    IsolatedDpState candidate)
    {
        var key = (selected, qty);

        if (!layer.TryGetValue(key, out var current)
            || candidate.Cost + CostEpsilon < current.Cost)
        {
            layer[key] = candidate;
        }
    }

    private static IsolatedCardSolution SolveIsolatedCardExact(
    IReadOnlyList<CanonicalSeller> sellers,
    IReadOnlyList<int> sellerIndices,
    int cardIndex,
    int requiredQuantity)
    {
        var sellerCount = sellerIndices.Count;

        if (sellerCount == 0 || requiredQuantity <= 0)
        {
            return new IsolatedCardSolution(cardIndex, [], false);
        }

        var maxSelectedSellers = Math.Min(sellerCount, requiredQuantity);

        var layers = new Dictionary<(int Selected, int Qty), IsolatedDpState>[sellerCount + 1];
        layers[0] = new Dictionary<(int Selected, int Qty), IsolatedDpState>
        {
            [(0, 0)] = new IsolatedDpState(0m, 0, 0, 0)
        };

        for (var sellerOffset = 0; sellerOffset < sellerCount; sellerOffset++)
        {
            var canonicalSeller = sellers[sellerIndices[sellerOffset]];
            var profile = canonicalSeller.CardProfiles[cardIndex];

            var maxTake = profile is null
                ? 0
                : Math.Min(requiredQuantity, profile.QtyUsable);

            var currentLayer = layers[sellerOffset];
            var nextLayer = new Dictionary<(int Selected, int Qty), IsolatedDpState>(currentLayer.Count * 2);
            layers[sellerOffset + 1] = nextLayer;

            foreach (var stateEntry in currentLayer)
            {
                var selected = stateEntry.Key.Selected;
                var qty = stateEntry.Key.Qty;
                var currentCost = stateEntry.Value.Cost;

                // Skip seller
                UpsertIsolatedDpState(
                    nextLayer,
                    selected,
                    qty,
                    new IsolatedDpState(currentCost, selected, qty, 0));

                if (maxTake <= 0 || selected >= maxSelectedSellers)
                {
                    continue;
                }

                for (var take = 1; take <= maxTake && qty + take <= requiredQuantity; take++)
                {
                    var nextSelected = selected + 1;
                    var nextQty = qty + take;
                    var nextCost = currentCost + profile!.PrefixCosts[take];

                    UpsertIsolatedDpState(
                        nextLayer,
                        nextSelected,
                        nextQty,
                        new IsolatedDpState(nextCost, selected, qty, take));
                }
            }
        }

        var finalLayer = layers[sellerCount];
        var bestSelectedCount = -1;
        var bestCost = decimal.MaxValue;

        for (var selected = 1; selected <= maxSelectedSellers; selected++)
        {
            if (!finalLayer.TryGetValue((selected, requiredQuantity), out var state))
            {
                continue;
            }

            var candidateCost = state.Cost;

            if (bestSelectedCount == -1
                || selected < bestSelectedCount
                || (selected == bestSelectedCount && candidateCost + CostEpsilon < bestCost))
            {
                bestSelectedCount = selected;
                bestCost = candidateCost;
            }
        }

        if (bestSelectedCount <= 0 || bestCost == decimal.MaxValue)
        {
            return new IsolatedCardSolution(cardIndex, [], false);
        }

        var selectedSellerNames = new List<string>(bestSelectedCount);
        var traceKey = (Selected: bestSelectedCount, Qty: requiredQuantity);

        for (var sellerOffset = sellerCount; sellerOffset >= 1; sellerOffset--)
        {
            var layer = layers[sellerOffset];

            if (!layer.TryGetValue(traceKey, out var traceState))
            {
                return new IsolatedCardSolution(cardIndex, [], false);
            }

            if (traceState.Take > 0)
            {
                selectedSellerNames.Add(sellers[sellerIndices[sellerOffset - 1]].SellerName);
            }

            traceKey = (traceState.PreviousSelected, traceState.PreviousQty);
        }

        if (traceKey != (0, 0))
        {
            return new IsolatedCardSolution(cardIndex, [], false);
        }

        selectedSellerNames.Sort(StringComparer.OrdinalIgnoreCase);

        return new IsolatedCardSolution(cardIndex, selectedSellerNames, true);
    }

    private static IsolatedCardsResult SolveIsolatedSingleCardCards(
    IReadOnlyList<CanonicalSeller> sellers,
    int[] requiredByCard)
    {
        var sellersByCard = BuildSellersByCard(sellers, requiredByCard.Length);
        var solutions = new List<IsolatedCardSolution>();
        var impossibleCardIndices = new List<int>();

        for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
        {
            var requiredQty = requiredByCard[cardIndex];
            if (requiredQty <= 0)
            {
                continue;
            }

            var sellerIndices = sellersByCard[cardIndex];
            if (sellerIndices.Count == 0)
            {
                impossibleCardIndices.Add(cardIndex);
                continue;
            }

            var allSingleCard = true;
            for (var offset = 0; offset < sellerIndices.Count; offset++)
            {
                if (sellers[sellerIndices[offset]].ActiveCardCount != 1)
                {
                    allSingleCard = false;
                    break;
                }
            }

            if (!allSingleCard)
            {
                continue;
            }

            var localSolution = SolveIsolatedCardExact(
                sellers,
                sellerIndices,
                cardIndex,
                requiredQty);

            if (!localSolution.IsSolved)
            {
                impossibleCardIndices.Add(cardIndex);
                continue;
            }

            solutions.Add(localSolution);
        }

        return new IsolatedCardsResult(solutions, impossibleCardIndices);
    }






    private readonly record struct IsolatedDpState(
    decimal Cost,
    int PreviousSelected,
    int PreviousQty,
    int Take);
    
    private sealed record IsolatedCardsResult(
    IReadOnlyList<IsolatedCardSolution> Solutions,
    IReadOnlyList<int> ImpossibleCardIndices);
    
    private sealed record IsolatedCardSolution(
        int CardIndex,
        IReadOnlyList<string> SelectedSellerNames,
        bool IsSolved);

    private sealed record PurgePipelineResult(
    IReadOnlyList<CanonicalSeller> RemainingSellers,
    int[] RequiredByCard,
    HashSet<string> PreselectedSellerNames,
    HashSet<int> UncoveredCardIndices,
    IReadOnlyList<OptimizationPhaseProfile> ProfilePhases,
    int TotalRemovedUseless,
    int TotalRemovedSingleCardDominated,
    int TotalRemovedGlobalDominated,
    int InitialSellerCount,
    int Rounds);

    private sealed record PreprocessResult(
    IReadOnlyList<CanonicalSeller> Sellers,
    int RemovedUseless,
    int RemovedSingleCardDominated,
    int RemovedGlobalDominated);
    
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
    
    private sealed class SellerCardProfile
    {
        public SellerCardProfile(decimal[] prefixCosts)
        {
            PrefixCosts = prefixCosts;
        }

        public decimal[] PrefixCosts { get; }

        public int QtyUsable => PrefixCosts.Length - 1;
    }
    
}
