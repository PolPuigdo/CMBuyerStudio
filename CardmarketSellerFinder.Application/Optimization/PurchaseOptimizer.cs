using CMBuyerStudio.Application.Common.Options;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;
using CardmarketSellerFinder.Engine;
using CardmarketSellerFinder.Models;
using CardmarketSellerFinder.Options;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

using System.Diagnostics;

namespace CMBuyerStudio.Application.Optimization;

public sealed class PurchaseOptimizer
{
    private const decimal CostEpsilon = 0.000001m;
    private const decimal InfiniteCost = decimal.MaxValue / 4m;
    private const int LegacySmallPoolBruteForceMaxSellerCount = 14;
    private const int LegacyQuickExactMaxK = 4;
    private const int LegacyQuickProvisionalK = 4;
    private const double LegacyQuickExactCombinationLimit = 2_500_000d;
    private const int LegacyFallbackRescueMaxCandidateSellers = 30;
    private const int LegacyFallbackRescueTopProvidersPerCard = 5;
    private const int LegacyFallbackRescueGlobalCandidates = 20;

    private readonly PurchaseOptimizerOptions _options;

    public PurchaseOptimizer(IOptions<PurchaseOptimizerOptions> options)
    {
        _options = options?.Value ?? new PurchaseOptimizerOptions();
    }

    public PurchaseOptimizationResult Optimize(PurgedScopeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var profilePhases = new List<OptimizationPhaseProfile>();
        var selectedSellerNames = new HashSet<string>(
            snapshot.PreselectedSellerNames,
            StringComparer.OrdinalIgnoreCase);
        var knownUncoveredCardKeys = new HashSet<string>(
            snapshot.UncoveredCardKeys,
            StringComparer.OrdinalIgnoreCase);

        if (snapshot.ScopedMarketData.Count == 0)
        {
            return BuildResult(snapshot, selectedSellerNames, knownUncoveredCardKeys, profilePhases);
        }

        var requiredByCard = snapshot.PurgedMarketData
            .Select(card => ResolveRemainingRequiredQuantity(
                ResolveCardKey(card.Target),
                card.Target.DesiredQuantity,
                snapshot.RemainingRequiredByCardKey))
            .ToArray();

        if (requiredByCard.Length == 0 || requiredByCard.All(quantity => quantity <= 0))
        {
            return BuildResult(snapshot, selectedSellerNames, knownUncoveredCardKeys, profilePhases);
        }

        var settings = ResolveRuntimeSettings(_options);
        var canonicalBuildStopwatch = Stopwatch.StartNew();
        var canonicalBuild = BuildCanonicalSellersWithMetrics(
            snapshot.PurgedMarketData,
            requiredByCard,
            snapshot.FixedCostBySellerName);
        canonicalBuildStopwatch.Stop();
        var canonicalSellers = canonicalBuild.Sellers;
        profilePhases.Add(CreateCanonicalBuildPhase(
            "Optimize.BuildCanonicalSellers",
            canonicalBuildStopwatch.ElapsedMilliseconds,
            canonicalBuild.Metrics));

        if (canonicalSellers.Count == 0)
        {
            AddUncoveredCardKeys(snapshot.PurgedMarketData, requiredByCard, knownUncoveredCardKeys);
            return BuildResult(snapshot, selectedSellerNames, knownUncoveredCardKeys, profilePhases);
        }

        var activeRequiredByCard = requiredByCard.ToArray();
        if (activeRequiredByCard.All(quantity => quantity <= 0))
        {
            return BuildResult(snapshot, selectedSellerNames, knownUncoveredCardKeys, profilePhases);
        }

        var effectiveParallelism = ResolveParallelism();
        var candidatePoolStopwatch = Stopwatch.StartNew();
        var candidatePoolBuilder = new CandidatePoolBuilder(effectiveParallelism);
        var candidatePoolResult = candidatePoolBuilder.BuildCandidateSellerNames(
            canonicalSellers,
            snapshot.PurgedMarketData.Select(card => card.Target.CardName).ToArray(),
            activeRequiredByCard,
            snapshot.PreselectedSellerNames,
            settings);
        candidatePoolStopwatch.Stop();
        var candidateSellerNames = candidatePoolResult.CandidateSellerNames;
        if (canonicalSellers.Count <= LegacySmallPoolBruteForceMaxSellerCount)
        {
            candidateSellerNames = canonicalSellers
                .Select(seller => seller.SellerName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        profilePhases.Add(new OptimizationPhaseProfile
        {
            Name = "Optimize.CandidatePool",
            ElapsedMilliseconds = candidatePoolStopwatch.ElapsedMilliseconds,
            Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["canonicalSellers"] = canonicalSellers.Count,
                ["candidateSellers"] = candidateSellerNames.Count,
                ["rareProviders"] = candidatePoolResult.RareProviderCount,
                ["feasibilityRescues"] = candidatePoolResult.FeasibilityRescueCount,
                ["coveragePromotions"] = candidatePoolResult.CoveragePromotionCount,
                ["topHitPromotions"] = candidatePoolResult.TopHitPromotionCount
            },
            Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidatePoolMin"] = settings.CandidatePoolMin.ToString(),
                ["candidatePoolMax"] = settings.CandidatePoolMax.ToString(),
                ["smallPoolExpandedToAllCanonical"] = canonicalSellers.Count <= LegacySmallPoolBruteForceMaxSellerCount ? "true" : "false"
            },
            Details = candidatePoolResult.CardDetails
        });

        var reducedSellers = canonicalSellers
            .Where(seller => candidateSellerNames.Contains(seller.SellerName))
            .ToList();

        if (reducedSellers.Count == 0)
        {
            AddUncoveredCardKeys(snapshot.PurgedMarketData, activeRequiredByCard, knownUncoveredCardKeys);
            return BuildResult(snapshot, selectedSellerNames, knownUncoveredCardKeys, profilePhases);
        }

        var cardCount = activeRequiredByCard.Length;
        var orderedReducedSellers = OrderSellersForSearch(
            reducedSellers,
            activeRequiredByCard,
            cardCount).ToList();

        var quickSolveStopwatch = Stopwatch.StartNew();
        var quickSolveResult = SolveLegacyCompatibleSelection(
            orderedReducedSellers,
            activeRequiredByCard,
            effectiveParallelism,
            settings);
        quickSolveStopwatch.Stop();
        profilePhases.Add(new OptimizationPhaseProfile
        {
            Name = "Optimize.ReducedExact",
            ElapsedMilliseconds = quickSolveStopwatch.ElapsedMilliseconds,
            Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateSellers"] = orderedReducedSellers.Count,
                ["exactSelectionSize"] = quickSolveResult.SelectedSellerNames.Count,
                ["isFullCoverage"] = quickSolveResult.IsFullCoverage ? 1 : 0,
                ["parallelism"] = effectiveParallelism,
                ["lowerBoundSellerCount"] = quickSolveResult.Metrics.LowerBoundSellerCount,
                ["targetKsEvaluated"] = quickSolveResult.Metrics.TargetKsEvaluated,
                ["deadlineHit"] = 0,
                ["bestCostUpdates"] = quickSolveResult.Metrics.BestCostUpdates,
                ["partitionCount"] = quickSolveResult.Metrics.PartitionCount
            },
            Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacyQuickExactMaxK"] = LegacyQuickExactMaxK.ToString(),
                ["legacyQuickProvisionalK"] = LegacyQuickProvisionalK.ToString(),
                ["exactMaxK"] = settings.ExactMaxK.ToString(),
                ["solverTimeBudgetMinutes"] = settings.SolverTimeBudgetMinutes.ToString(),
                ["incumbentSource"] = quickSolveResult.Metrics.IncumbentSource
            }
        });

        var activeSelection = quickSolveResult.SelectedSellerNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (quickSolveResult.IsFullCoverage)
        {
            if (settings.EnableFinalCostRefine
                && !IsExactIncumbentSource(quickSolveResult.Metrics.IncumbentSource)
                && activeSelection.Count > 0
                && activeSelection.Count <= settings.ExactMaxK)
            {
                var refineCombinationCount = CalculateCombinationCount(
                    orderedReducedSellers.Count,
                    activeSelection.Count);

                if (!double.IsNaN(refineCombinationCount)
                    && refineCombinationCount <= LegacyQuickExactCombinationLimit)
                {
                var refineStopwatch = Stopwatch.StartNew();
                activeSelection = RefineSelectionForFixedSellerCountOnOrderedCanonical(
                    orderedReducedSellers,
                    activeRequiredByCard,
                    activeSelection,
                    effectiveParallelism);
                refineStopwatch.Stop();
                profilePhases.Add(new OptimizationPhaseProfile
                {
                    Name = "Optimize.FinalRefine",
                    ElapsedMilliseconds = refineStopwatch.ElapsedMilliseconds,
                    Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["selectedSellers"] = activeSelection.Count,
                        ["parallelism"] = effectiveParallelism,
                        ["reusedExactContext"] = 0
                    }
                });
                }
            }

            if (activeSelection.Count > 1)
            {
                var pruneStopwatch = Stopwatch.StartNew();
                activeSelection = PruneRedundantFullCoverageSelection(
                    orderedReducedSellers,
                    activeRequiredByCard,
                    activeSelection);
                pruneStopwatch.Stop();
                profilePhases.Add(new OptimizationPhaseProfile
                {
                    Name = "Optimize.FullCoveragePrune",
                    ElapsedMilliseconds = pruneStopwatch.ElapsedMilliseconds,
                    Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["selectedSellers"] = activeSelection.Count
                    }
                });
            }
        }

        if (string.Equals(quickSolveResult.Metrics.IncumbentSource, "legacy-fallback", StringComparison.OrdinalIgnoreCase))
        {
            var legacyOverrideStopwatch = Stopwatch.StartNew();
            var legacyOverride = TryLegacyFallbackOverride(
                snapshot,
                activeRequiredByCard,
                activeSelection,
                snapshot.PreselectedSellerNames,
                settings,
                orderedReducedSellers);
            legacyOverrideStopwatch.Stop();

            if (legacyOverride.Applied)
            {
                activeSelection = legacyOverride.SelectedSellerNames
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            profilePhases.Add(new OptimizationPhaseProfile
            {
                Name = "Optimize.LegacyFallbackOverride",
                ElapsedMilliseconds = legacyOverrideStopwatch.ElapsedMilliseconds,
                Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["applied"] = legacyOverride.Applied ? 1 : 0,
                    ["legacySelectedSellers"] = legacyOverride.SelectedSellerNames.Count,
                    ["currentSelectedSellers"] = activeSelection.Count,
                    ["currentExtraSellers"] = legacyOverride.CurrentExtraSellerCount,
                    ["legacyExtraSellers"] = legacyOverride.LegacyExtraSellerCount
                },
                Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reason"] = legacyOverride.Reason,
                    ["currentCost"] = legacyOverride.CurrentCost.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    ["legacyCost"] = legacyOverride.LegacyCost.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    ["shippingPenaltyFloor"] = legacyOverride.ShippingPenaltyFloor.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    ["currentShippingPenalty"] = legacyOverride.CurrentShippingPenalty.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    ["legacyShippingPenalty"] = legacyOverride.LegacyShippingPenalty.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    ["currentShippingAwareCost"] = legacyOverride.CurrentShippingAwareCost.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    ["legacyShippingAwareCost"] = legacyOverride.LegacyShippingAwareCost.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                }
            });
        }

        selectedSellerNames.UnionWith(activeSelection);

        return BuildResult(snapshot, selectedSellerNames, knownUncoveredCardKeys, profilePhases);
    }

    private static PurchaseOptimizationResult BuildResult(
        PurgedScopeSnapshot snapshot,
        IReadOnlyCollection<string> selectedSellerNames,
        IReadOnlySet<string> knownUncoveredCardKeys,
        IReadOnlyList<OptimizationPhaseProfile>? existingPhases = null)
    {
        var profilePhases = existingPhases?.ToList() ?? [];
        var assignmentsStopwatch = Stopwatch.StartNew();
        var assignments = BuildAssignments(
            snapshot.ScopedMarketData,
            selectedSellerNames,
            out var uncoveredCards,
            out var totalCardsCost);
        assignmentsStopwatch.Stop();
        profilePhases.Add(new OptimizationPhaseProfile
        {
            Name = "Optimize.BuildAssignments",
            ElapsedMilliseconds = assignmentsStopwatch.ElapsedMilliseconds,
            Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["assignments"] = assignments.Count,
                ["selectedSellers"] = selectedSellerNames.Count,
                ["uncoveredCards"] = uncoveredCards.Count
            }
        });

        var validationStopwatch = Stopwatch.StartNew();
        var assignmentValidation = ValidateAssignmentsAndRepairResidual(
            snapshot.ScopedMarketData,
            snapshot.FixedCostBySellerName,
            selectedSellerNames,
            assignments,
            uncoveredCards);
        validationStopwatch.Stop();
        profilePhases.Add(new OptimizationPhaseProfile
        {
            Name = "Optimize.AssignmentValidation",
            ElapsedMilliseconds = validationStopwatch.ElapsedMilliseconds,
            Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["targetUnits"] = snapshot.ScopedMarketData.Sum(card => card.Target.DesiredQuantity),
                ["initialAssignedUnits"] = assignments.Sum(assignment => assignment.Quantity),
                ["finalAssignedUnits"] = assignmentValidation.Assignments.Sum(assignment => assignment.Quantity),
                ["initialSelectedSellers"] = selectedSellerNames.Count,
                ["finalSelectedSellers"] = assignmentValidation.SelectedSellerNames.Count,
                ["initialUncoveredCards"] = uncoveredCards.Count,
                ["residualFixupAddedSellers"] = assignmentValidation.AddedSellerNames.Count,
                ["finalUncoveredCards"] = assignmentValidation.UncoveredCardKeys.Count,
                ["finalAssignments"] = assignmentValidation.Assignments.Count
            },
            Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fixupApplied"] = assignmentValidation.AddedSellerNames.Count > 0 ? "true" : "false",
                ["addedSellers"] = string.Join(", ", assignmentValidation.AddedSellerNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            }
        });

        var uncoveredCardKeys = assignmentValidation.UncoveredCardKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        uncoveredCardKeys.UnionWith(knownUncoveredCardKeys);

        var selectedSellers = assignmentValidation.SelectedSellerNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PurchaseOptimizationResult
        {
            SelectedSellerNames = selectedSellers,
            Assignments = assignmentValidation.Assignments,
            SellerCount = selectedSellers.Count,
            CardsTotalPrice = assignmentValidation.CardsTotalPrice,
            UncoveredCardKeys = uncoveredCardKeys,
            ProfilePhases = profilePhases
        };
    }

    private static LegacyQuickSolveResult SolveLegacyCompatibleSelection(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int effectiveParallelism,
        RuntimeSettings settings)
    {
        if (orderedSellers.Count == 0)
        {
            return new LegacyQuickSolveResult(
                [],
                IsFullCoverage: false,
                new LegacyQuickSolveMetrics(
                    LowerBoundSellerCount: 0,
                    TargetKsEvaluated: 0,
                    BestCostUpdates: 0,
                    PartitionCount: 0,
                    IncumbentSource: "empty"));
        }

        var cardCount = requiredByCard.Length;
        var sellerCount = orderedSellers.Count;
        var sellerCardQty = BuildSellerCardQtyMatrix(orderedSellers, cardCount);
        var suffixCoverage = BuildSuffixCoverageMatrix(sellerCardQty, sellerCount, cardCount);

        var impossibleCardIndices = FindImpossibleCardIndices(requiredByCard, sellerCardQty, sellerCount);
        if (impossibleCardIndices.Count > 0)
        {
            return new LegacyQuickSolveResult(
                [],
                IsFullCoverage: false,
                new LegacyQuickSolveMetrics(
                    LowerBoundSellerCount: 0,
                    TargetKsEvaluated: 0,
                    BestCostUpdates: 0,
                    PartitionCount: 0,
                    IncumbentSource: "impossible"));
        }

        if (sellerCount <= LegacySmallPoolBruteForceMaxSellerCount)
        {
            var bruteForceSelection = FindBestSelectionByObjective(
                orderedSellers,
                requiredByCard,
                cardCount);

            return new LegacyQuickSolveResult(
                bruteForceSelection.SelectedOrderedSellerIndices
                    .Select(index => orderedSellers[index].SellerName)
                    .ToArray(),
                IsFullCoverage: bruteForceSelection.UncoveredCardIndices.Count == 0,
                new LegacyQuickSolveMetrics(
                    LowerBoundSellerCount: 0,
                    TargetKsEvaluated: bruteForceSelection.SelectedOrderedSellerIndices.Count,
                    BestCostUpdates: bruteForceSelection.SelectionCountEvaluated,
                    PartitionCount: 1,
                    IncumbentSource: "legacy-small-bruteforce"));
        }

        var lowerBoundSellerCount = CalculateLowerBoundSellerCount(requiredByCard, sellerCardQty, cardCount, sellerCount);
        var greedyUpperBoundSelection = BuildGreedyUpperBoundSelection(requiredByCard, sellerCardQty, cardCount, sellerCount);
        if (greedyUpperBoundSelection is null)
        {
            return new LegacyQuickSolveResult(
                [],
                IsFullCoverage: false,
                new LegacyQuickSolveMetrics(
                    LowerBoundSellerCount: lowerBoundSellerCount,
                    TargetKsEvaluated: 0,
                    BestCostUpdates: 0,
                    PartitionCount: 0,
                    IncumbentSource: "no-upper-bound"));
        }

        var greedySelectionOrdered = greedyUpperBoundSelection.OrderBy(index => index).ToList();
        var exactUpperBoundSellerCount = Math.Min(Math.Min(LegacyQuickExactMaxK, settings.ExactMaxK), sellerCount);
        SelectionResult? bestExactSelection = null;
        var targetKsEvaluated = 0;
        var bestCostUpdates = 0;
        var partitionCount = 0;

        if (lowerBoundSellerCount <= exactUpperBoundSellerCount)
        {
            var searchContext = BuildSearchPrecomputationContext(orderedSellers, requiredByCard, cardCount);
            EnsureSearchContextHasSuffixMinimumCosts(searchContext, orderedSellers, requiredByCard);

            for (var targetSellerCount = lowerBoundSellerCount; targetSellerCount <= exactUpperBoundSellerCount; targetSellerCount++)
            {
                var combinationCount = CalculateCombinationCount(sellerCount, targetSellerCount);
                if (double.IsNaN(combinationCount) || combinationCount > LegacyQuickExactCombinationLimit)
                {
                    continue;
                }

                targetKsEvaluated++;
                var initialSelection = greedySelectionOrdered.Count == targetSellerCount
                    ? greedySelectionOrdered
                    : null;
                var initialUpperBoundCost = initialSelection is null
                    ? InfiniteCost
                    : CalculateExactCostForSelection(orderedSellers, requiredByCard, cardCount, initialSelection);

                var phaseB = FindBestSelectionForFixedK(
                    targetSellerCount,
                    requiredByCard,
                    orderedSellers,
                    searchContext,
                    effectiveParallelism,
                    initialUpperBoundCost,
                    initialSelection);

                partitionCount += phaseB.PartitionCount;
                if (phaseB.BestSelection is null || IsInfinite(phaseB.BestSelection.TotalCost))
                {
                    continue;
                }

                if (IsBetterCandidate(phaseB.BestSelection, bestExactSelection))
                {
                    bestExactSelection = phaseB.BestSelection;
                    bestCostUpdates++;
                }
            }
        }

        if (bestExactSelection is not null)
        {
            return new LegacyQuickSolveResult(
                bestExactSelection.SelectedOrderedSellerIndices
                    .Select(index => orderedSellers[index].SellerName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                IsFullCoverage: true,
                new LegacyQuickSolveMetrics(
                    LowerBoundSellerCount: lowerBoundSellerCount,
                    TargetKsEvaluated: targetKsEvaluated,
                    BestCostUpdates: bestCostUpdates,
                    PartitionCount: partitionCount,
                    IncumbentSource: "legacy-quick-exact"));
        }

        var provisionalSelection = BuildGreedyProvisionalSelection(
            orderedSellers,
            requiredByCard,
            cardCount,
            Math.Min(LegacyQuickProvisionalK, sellerCount));
        provisionalSelection = RefineSelectionWithSingleSwap(
            orderedSellers,
            requiredByCard,
            cardCount,
            provisionalSelection);

        var selectedSellerNames = provisionalSelection
            .Select(index => orderedSellers[index].SellerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplyLegacyFallbackForRemainingCards(
            orderedSellers,
            requiredByCard,
            selectedSellerNames,
            new HashSet<int>());

        var incumbentSource = selectedSellerNames.Count == 0 ? "empty-provisional" : "legacy-fallback";
        if (selectedSellerNames.Count > 0
            && IsSelectionFullyCoveredByNames(orderedSellers, requiredByCard, selectedSellerNames))
        {
            var fallbackRescue = TryRescueFallbackSelection(
                orderedSellers,
                requiredByCard,
                selectedSellerNames,
                effectiveParallelism,
                settings);
            targetKsEvaluated += fallbackRescue.TargetKsEvaluated;
            bestCostUpdates += fallbackRescue.BestCostUpdates;
            partitionCount += fallbackRescue.PartitionCount;

            if (fallbackRescue.RescuedSelection is not null)
            {
                selectedSellerNames = fallbackRescue.RescuedSelection
                    .Select(index => orderedSellers[index].SellerName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                incumbentSource = "legacy-fallback-rescue-exact";
            }
        }

        return new LegacyQuickSolveResult(
            selectedSellerNames,
            IsSelectionFullyCoveredByNames(orderedSellers, requiredByCard, selectedSellerNames),
            new LegacyQuickSolveMetrics(
                LowerBoundSellerCount: lowerBoundSellerCount,
                TargetKsEvaluated: targetKsEvaluated,
                BestCostUpdates: bestCostUpdates,
                PartitionCount: partitionCount,
                IncumbentSource: incumbentSource));
    }

    private static HashSet<int>? BuildGreedyUpperBoundSelection(
        int[] requiredQuantities,
        int[,] sellerCardQty,
        int cardCount,
        int sellerCount)
    {
        var selectedSellers = new HashSet<int>();
        var remaining = requiredQuantities.ToArray();

        while (remaining.Any(quantity => quantity > 0))
        {
            var bestSeller = -1;
            var bestContribution = 0;

            for (var sellerIndex = 0; sellerIndex < sellerCount; sellerIndex++)
            {
                if (selectedSellers.Contains(sellerIndex))
                {
                    continue;
                }

                var contribution = 0;
                for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
                {
                    contribution += Math.Min(remaining[cardIndex], sellerCardQty[sellerIndex, cardIndex]);
                }

                if (contribution > bestContribution)
                {
                    bestContribution = contribution;
                    bestSeller = sellerIndex;
                }
            }

            if (bestSeller < 0 || bestContribution == 0)
            {
                return null;
            }

            selectedSellers.Add(bestSeller);
            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                remaining[cardIndex] = Math.Max(0, remaining[cardIndex] - sellerCardQty[bestSeller, cardIndex]);
            }
        }

        return selectedSellers;
    }

    private static void ApplyLegacyFallbackForRemainingCards(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        HashSet<string> selectedSellerNames,
        IReadOnlySet<int> knownUncoveredCardIndices)
    {
        while (true)
        {
            var remainingRequiredByCard = BuildRemainingRequiredByCard(
                orderedSellers,
                requiredByCard,
                selectedSellerNames,
                knownUncoveredCardIndices);

            if (remainingRequiredByCard.All(quantity => quantity <= 0))
            {
                break;
            }

            var bestPackSeller = orderedSellers
                .Where(seller => !selectedSellerNames.Contains(seller.SellerName) && seller.ActiveCardCount >= 2)
                .Select(seller =>
                {
                    var coveredCards = 0;
                    var coveredUnits = 0;
                    var oneUnitCost = seller.FixedCost;

                    foreach (var cardIndex in seller.ActiveCards)
                    {
                        var requiredQty = remainingRequiredByCard[cardIndex];
                        if (requiredQty <= 0)
                        {
                            continue;
                        }

                        coveredCards++;
                        coveredUnits += Math.Min(requiredQty, seller.QtyByCard[cardIndex]);
                        var profile = seller.CardProfiles[cardIndex];
                        if (profile is not null && profile.QtyUsable > 0)
                        {
                            oneUnitCost = AddCost(oneUnitCost, profile.PrefixCosts[1]);
                        }
                    }

                    return new FallbackPackCandidate(
                        seller.SellerName,
                        coveredCards,
                        coveredUnits,
                        oneUnitCost,
                        seller.OriginalOrder);
                })
                .OrderByDescending(candidate => candidate.CoveredCards)
                .ThenByDescending(candidate => candidate.CoveredUnits)
                .ThenBy(candidate => candidate.OneUnitPackCost)
                .ThenBy(candidate => candidate.SellerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.OriginalOrder)
                .FirstOrDefault();

            if (bestPackSeller is null || bestPackSeller.CoveredCards < 2)
            {
                break;
            }

            selectedSellerNames.Add(bestPackSeller.SellerName);
        }

        var remainingBeforeSingles = BuildRemainingRequiredByCard(
            orderedSellers,
            requiredByCard,
            selectedSellerNames,
            knownUncoveredCardIndices);

        for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
        {
            if (knownUncoveredCardIndices.Contains(cardIndex) || remainingBeforeSingles[cardIndex] <= 0)
            {
                continue;
            }

            var remainingQty = remainingBeforeSingles[cardIndex];
            var orderedProviders = orderedSellers
                .Where(seller => seller.QtyByCard[cardIndex] > 0)
                .OrderBy(seller => seller.CardProfiles[cardIndex]!.PrefixCosts[1])
                .ThenByDescending(seller => seller.QtyByCard[cardIndex])
                .ThenBy(seller => seller.SellerName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var seller in orderedProviders)
            {
                if (remainingQty <= 0)
                {
                    break;
                }

                selectedSellerNames.Add(seller.SellerName);
                remainingQty -= seller.QtyByCard[cardIndex];
            }
        }
    }

    private static FallbackRescueResult TryRescueFallbackSelection(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlySet<string> fallbackSelection,
        int effectiveParallelism,
        RuntimeSettings settings)
    {
        var rescueCandidateNames = BuildFallbackRescueCandidateSellerNames(
            orderedSellers,
            requiredByCard,
            fallbackSelection);
        var rescueSellers = orderedSellers
            .Where(seller => rescueCandidateNames.Contains(seller.SellerName))
            .ToList();
        if (rescueSellers.Count == 0)
        {
            return new FallbackRescueResult(null, 0, 0, 0);
        }

        var cardCount = requiredByCard.Length;
        var rescueSellerCardQty = BuildSellerCardQtyMatrix(rescueSellers, cardCount);
        var rescueLowerBound = CalculateLowerBoundSellerCount(
            requiredByCard,
            rescueSellerCardQty,
            cardCount,
            rescueSellers.Count);
        var rescueUpperBound = Math.Min(settings.ExactMaxK, rescueSellers.Count);
        if (rescueLowerBound > rescueUpperBound)
        {
            return new FallbackRescueResult(null, 0, 0, 0);
        }

        var searchContext = BuildSearchPrecomputationContext(rescueSellers, requiredByCard, cardCount);
        EnsureSearchContextHasSuffixMinimumCosts(searchContext, rescueSellers, requiredByCard);

        SelectionResult? best = null;
        var targetKsEvaluated = 0;
        var bestCostUpdates = 0;
        var partitionCount = 0;

        for (var targetSellerCount = rescueLowerBound; targetSellerCount <= rescueUpperBound; targetSellerCount++)
        {
            var combinationCount = CalculateCombinationCount(rescueSellers.Count, targetSellerCount);
            if (double.IsNaN(combinationCount) || combinationCount > LegacyQuickExactCombinationLimit)
            {
                continue;
            }

            targetKsEvaluated++;
            IReadOnlyList<int>? initialSelection = null;
            var initialUpperBoundCost = InfiniteCost;

            if (fallbackSelection.Count == targetSellerCount)
            {
                var mappedSelection = rescueSellers
                    .Select((seller, index) => new { seller.SellerName, Index = index })
                    .Where(item => fallbackSelection.Contains(item.SellerName))
                    .Select(item => item.Index)
                    .OrderBy(index => index)
                    .ToArray();

                if (mappedSelection.Length == targetSellerCount)
                {
                    initialSelection = mappedSelection;
                    initialUpperBoundCost = CalculateExactCostForSelection(
                        rescueSellers,
                        requiredByCard,
                        cardCount,
                        mappedSelection);
                }
            }

            var phaseB = FindBestSelectionForFixedK(
                targetSellerCount,
                requiredByCard,
                rescueSellers,
                searchContext,
                effectiveParallelism,
                initialUpperBoundCost,
                initialSelection);
            partitionCount += phaseB.PartitionCount;

            if (phaseB.BestSelection is null || IsInfinite(phaseB.BestSelection.TotalCost))
            {
                continue;
            }

            if (IsBetterCandidate(phaseB.BestSelection, best))
            {
                best = phaseB.BestSelection;
                bestCostUpdates++;
            }
        }

        if (best is null)
        {
            return new FallbackRescueResult(null, targetKsEvaluated, bestCostUpdates, partitionCount);
        }

        var orderedIndexBySellerName = orderedSellers
            .Select((seller, index) => new { seller.SellerName, Index = index })
            .ToDictionary(item => item.SellerName, item => item.Index, StringComparer.OrdinalIgnoreCase);

        var remappedSelection = best.SelectedOrderedSellerIndices
            .Select(index => orderedIndexBySellerName.GetValueOrDefault(rescueSellers[index].SellerName, -1))
            .Where(index => index >= 0)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (remappedSelection.Length == 0)
        {
            return new FallbackRescueResult(null, targetKsEvaluated, bestCostUpdates, partitionCount);
        }

        return new FallbackRescueResult(
            remappedSelection,
            targetKsEvaluated,
            bestCostUpdates,
            partitionCount);
    }

    private static HashSet<string> BuildFallbackRescueCandidateSellerNames(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlySet<string> fallbackSelection,
        int maxCandidateSellers = LegacyFallbackRescueMaxCandidateSellers)
    {
        maxCandidateSellers = Math.Max(1, maxCandidateSellers);
        var candidateNames = fallbackSelection.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (candidateNames.Count < maxCandidateSellers)
        {
            for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
            {
                if (requiredByCard[cardIndex] <= 0)
                {
                    continue;
                }

                foreach (var seller in orderedSellers
                    .Where(seller => seller.QtyByCard[cardIndex] > 0)
                    .OrderBy(seller => seller.CardProfiles[cardIndex]?.PrefixCosts[1] ?? InfiniteCost)
                    .ThenByDescending(seller => seller.QtyByCard[cardIndex])
                    .ThenBy(seller => seller.FixedCost)
                    .ThenBy(seller => seller.SellerName, StringComparer.OrdinalIgnoreCase)
                    .Take(LegacyFallbackRescueTopProvidersPerCard))
                {
                    candidateNames.Add(seller.SellerName);
                }
            }
        }

        if (candidateNames.Count < maxCandidateSellers)
        {
            var globalCandidateLimit = Math.Min(
                orderedSellers.Count,
                LegacyFallbackRescueGlobalCandidates + Math.Max(0, maxCandidateSellers - LegacyFallbackRescueMaxCandidateSellers));
            foreach (var seller in orderedSellers
                .OrderByDescending(seller => CoverageScore(seller, requiredByCard, requiredByCard.Length))
                .ThenBy(seller => seller.FixedCost)
                .ThenBy(seller => seller.SellerName, StringComparer.OrdinalIgnoreCase)
                .Take(globalCandidateLimit))
            {
                candidateNames.Add(seller.SellerName);
            }
        }

        if (candidateNames.Count <= maxCandidateSellers)
        {
            return candidateNames;
        }

        var prioritized = orderedSellers
            .Where(seller => candidateNames.Contains(seller.SellerName))
            .OrderByDescending(seller => CoverageScore(seller, requiredByCard, requiredByCard.Length))
            .ThenBy(seller => seller.CardProfiles
                .Where(profile => profile is not null && profile.QtyUsable > 0)
                .Select(profile => profile!.PrefixCosts[1])
                .DefaultIfEmpty(InfiniteCost)
                .Average())
            .ThenBy(seller => seller.FixedCost)
            .ThenBy(seller => seller.SellerName, StringComparer.OrdinalIgnoreCase)
            .Take(maxCandidateSellers)
            .Select(seller => seller.SellerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return prioritized;
    }

    private static bool IsExactIncumbentSource(string incumbentSource)
    {
        if (string.IsNullOrWhiteSpace(incumbentSource))
        {
            return false;
        }

        return incumbentSource.Contains("exact", StringComparison.OrdinalIgnoreCase)
            || incumbentSource.Contains("bruteforce", StringComparison.OrdinalIgnoreCase);
    }

    private static LegacyFallbackOverrideResult TryLegacyFallbackOverride(
        PurgedScopeSnapshot snapshot,
        int[] requiredByCard,
        IReadOnlySet<string> currentSelection,
        IReadOnlySet<string> preselectedSellerNames,
        RuntimeSettings settings,
        IReadOnlyList<CanonicalSeller> candidateSellers)
    {
        var shippingPenaltyFloor = ResolveLegacyFallbackShippingPenaltyFloor(snapshot.FixedCostBySellerName);
        var currentExtraSellerCount = CountLegacyFallbackExtraSellers(currentSelection, preselectedSellerNames);
        var currentShippingPenalty = CalculateLegacyFallbackShippingPenalty(
            currentSelection,
            preselectedSellerNames,
            snapshot.FixedCostBySellerName,
            shippingPenaltyFloor);
        var currentEffectiveSelection = currentSelection
            .Concat(preselectedSellerNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hasCurrentCost = TryCalculateScopedSelectionObjectiveCost(
            snapshot,
            currentEffectiveSelection,
            out var currentCost);
        if (!hasCurrentCost)
        {
            currentCost = InfiniteCost;
        }
        var currentShippingAwareCost = hasCurrentCost
            ? AddCost(currentCost, currentShippingPenalty)
            : InfiniteCost;

        var allowedSellerNames = BuildFallbackRescueCandidateSellerNames(
            candidateSellers,
            requiredByCard,
            currentSelection);
        var legacySnapshot = BuildLegacySnapshot(snapshot.PurgedMarketData, requiredByCard, allowedSellerNames);
        if (legacySnapshot.Cards.Count == 0)
        {
            return new LegacyFallbackOverrideResult(
                Applied: false,
                SelectedSellerNames: currentSelection.ToArray(),
                CurrentCost: currentCost,
                LegacyCost: InfiniteCost,
                CurrentShippingAwareCost: currentShippingAwareCost,
                LegacyShippingAwareCost: InfiniteCost,
                ShippingPenaltyFloor: shippingPenaltyFloor,
                CurrentShippingPenalty: currentShippingPenalty,
                LegacyShippingPenalty: 0m,
                CurrentExtraSellerCount: currentExtraSellerCount,
                LegacyExtraSellerCount: 0,
                Reason: "legacy-empty-snapshot");
        }

        try
        {
            var legacyFixedCosts = snapshot.FixedCostBySellerName.ToDictionary(
                pair => pair.Key,
                pair => (double)pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var solverTimeBudgetMinutes = Math.Min(settings.SolverTimeBudgetMinutes, 1);
            var legacyCandidatePoolMax = Math.Min(settings.CandidatePoolMax, 45);
            var legacyCandidatePoolMin = Math.Min(settings.CandidatePoolMin, legacyCandidatePoolMax);
            var overrideCandidateUniverse = candidateSellers
                .Select(seller => seller.SellerName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var legacySelection = RunLegacyFallbackSelection(
                legacySnapshot,
                legacyFixedCosts,
                settings,
                legacyCandidatePoolMin,
                legacyCandidatePoolMax,
                solverTimeBudgetMinutes);
            var usedExpandedCandidatePass = false;

            var legacyExtraSellerCount = CountLegacyFallbackExtraSellers(legacySelection, preselectedSellerNames);
            var legacyShippingPenalty = CalculateLegacyFallbackShippingPenalty(
                legacySelection,
                preselectedSellerNames,
                snapshot.FixedCostBySellerName,
                shippingPenaltyFloor);
            var legacyEffectiveSelection = legacySelection
                .Concat(preselectedSellerNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var hasLegacyCost = TryCalculateScopedSelectionObjectiveCost(
                snapshot,
                legacyEffectiveSelection,
                out var legacyCost);

            if (!hasLegacyCost)
            {
                var expandedAllowedSellerNames = snapshot.PurgedMarketData
                    .SelectMany(card => card.Offers)
                    .Where(offer => offer.AvailableQuantity > 0)
                    .Select(offer => offer.SellerName)
                    .Concat(currentSelection)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (expandedAllowedSellerNames.Count > allowedSellerNames.Count)
                {
                    var expandedSnapshot = BuildLegacySnapshot(
                        snapshot.PurgedMarketData,
                        requiredByCard,
                        expandedAllowedSellerNames);
                    if (expandedSnapshot.Cards.Count > 0)
                    {
                        var expandedCandidatePoolMax = legacyCandidatePoolMax;
                        var expandedCandidatePoolMin = legacyCandidatePoolMin;
                        legacySelection = RunLegacyFallbackSelection(
                            expandedSnapshot,
                            legacyFixedCosts,
                            settings,
                            expandedCandidatePoolMin,
                            expandedCandidatePoolMax,
                            solverTimeBudgetMinutes);
                        usedExpandedCandidatePass = true;

                        legacyExtraSellerCount = CountLegacyFallbackExtraSellers(legacySelection, preselectedSellerNames);
                        legacyShippingPenalty = CalculateLegacyFallbackShippingPenalty(
                            legacySelection,
                            preselectedSellerNames,
                            snapshot.FixedCostBySellerName,
                            shippingPenaltyFloor);
                        legacyEffectiveSelection = legacySelection
                            .Concat(preselectedSellerNames)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        hasLegacyCost = TryCalculateScopedSelectionObjectiveCost(
                            snapshot,
                            legacyEffectiveSelection,
                            out legacyCost);
                    }
                }
            }

            if (!hasLegacyCost)
            {
                return new LegacyFallbackOverrideResult(
                    Applied: false,
                    SelectedSellerNames: currentSelection.ToArray(),
                    CurrentCost: currentCost,
                    LegacyCost: InfiniteCost,
                    CurrentShippingAwareCost: currentShippingAwareCost,
                    LegacyShippingAwareCost: InfiniteCost,
                    ShippingPenaltyFloor: shippingPenaltyFloor,
                    CurrentShippingPenalty: currentShippingPenalty,
                    LegacyShippingPenalty: legacyShippingPenalty,
                    CurrentExtraSellerCount: currentExtraSellerCount,
                    LegacyExtraSellerCount: legacyExtraSellerCount,
                    Reason: usedExpandedCandidatePass ? "legacy-not-full-coverage-expanded" : "legacy-not-full-coverage");
            }

            var refinedLegacySelection = RefineLegacyFallbackOverrideSelection(
                snapshot,
                preselectedSellerNames,
                overrideCandidateUniverse,
                legacySelection,
                shippingPenaltyFloor);
            if (!refinedLegacySelection.SetEquals(legacySelection))
            {
                legacySelection = refinedLegacySelection;
                legacyExtraSellerCount = CountLegacyFallbackExtraSellers(legacySelection, preselectedSellerNames);
                legacyShippingPenalty = CalculateLegacyFallbackShippingPenalty(
                    legacySelection,
                    preselectedSellerNames,
                    snapshot.FixedCostBySellerName,
                    shippingPenaltyFloor);
                legacyEffectiveSelection = legacySelection
                    .Concat(preselectedSellerNames)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                hasLegacyCost = TryCalculateScopedSelectionObjectiveCost(
                    snapshot,
                    legacyEffectiveSelection,
                    out legacyCost);
                if (!hasLegacyCost)
                {
                    return new LegacyFallbackOverrideResult(
                        Applied: false,
                        SelectedSellerNames: currentSelection.ToArray(),
                        CurrentCost: currentCost,
                        LegacyCost: InfiniteCost,
                        CurrentShippingAwareCost: currentShippingAwareCost,
                        LegacyShippingAwareCost: InfiniteCost,
                        ShippingPenaltyFloor: shippingPenaltyFloor,
                        CurrentShippingPenalty: currentShippingPenalty,
                        LegacyShippingPenalty: legacyShippingPenalty,
                        CurrentExtraSellerCount: currentExtraSellerCount,
                        LegacyExtraSellerCount: legacyExtraSellerCount,
                        Reason: usedExpandedCandidatePass ? "legacy-refine-not-full-expanded" : "legacy-refine-not-full");
                }
            }

            var reasonSuffix = usedExpandedCandidatePass ? "-expanded" : string.Empty;
            var legacyShippingAwareCost = AddCost(legacyCost, legacyShippingPenalty);

            if (!hasCurrentCost)
            {
                return new LegacyFallbackOverrideResult(
                    Applied: true,
                    SelectedSellerNames: legacySelection.ToArray(),
                    CurrentCost: currentCost,
                    LegacyCost: legacyCost,
                    CurrentShippingAwareCost: currentShippingAwareCost,
                    LegacyShippingAwareCost: legacyShippingAwareCost,
                    ShippingPenaltyFloor: shippingPenaltyFloor,
                    CurrentShippingPenalty: currentShippingPenalty,
                    LegacyShippingPenalty: legacyShippingPenalty,
                    CurrentExtraSellerCount: currentExtraSellerCount,
                    LegacyExtraSellerCount: legacyExtraSellerCount,
                    Reason: $"legacy-covers-when-current-not{reasonSuffix}");
            }

            if (legacyShippingAwareCost + CostEpsilon < currentShippingAwareCost)
            {
                return new LegacyFallbackOverrideResult(
                    Applied: true,
                    SelectedSellerNames: legacySelection.ToArray(),
                    CurrentCost: currentCost,
                    LegacyCost: legacyCost,
                    CurrentShippingAwareCost: currentShippingAwareCost,
                    LegacyShippingAwareCost: legacyShippingAwareCost,
                    ShippingPenaltyFloor: shippingPenaltyFloor,
                    CurrentShippingPenalty: currentShippingPenalty,
                    LegacyShippingPenalty: legacyShippingPenalty,
                    CurrentExtraSellerCount: currentExtraSellerCount,
                    LegacyExtraSellerCount: legacyExtraSellerCount,
                    Reason: $"legacy-cheaper-shipping-aware{reasonSuffix}");
            }

            if (currentShippingAwareCost + CostEpsilon < legacyShippingAwareCost)
            {
                return new LegacyFallbackOverrideResult(
                    Applied: false,
                    SelectedSellerNames: currentSelection.ToArray(),
                    CurrentCost: currentCost,
                    LegacyCost: legacyCost,
                    CurrentShippingAwareCost: currentShippingAwareCost,
                    LegacyShippingAwareCost: legacyShippingAwareCost,
                    ShippingPenaltyFloor: shippingPenaltyFloor,
                    CurrentShippingPenalty: currentShippingPenalty,
                    LegacyShippingPenalty: legacyShippingPenalty,
                    CurrentExtraSellerCount: currentExtraSellerCount,
                    LegacyExtraSellerCount: legacyExtraSellerCount,
                    Reason: $"legacy-not-cheaper-shipping-aware{reasonSuffix}");
            }

            if (legacyCost + CostEpsilon < currentCost)
            {
                return new LegacyFallbackOverrideResult(
                    Applied: true,
                    SelectedSellerNames: legacySelection.ToArray(),
                    CurrentCost: currentCost,
                    LegacyCost: legacyCost,
                    CurrentShippingAwareCost: currentShippingAwareCost,
                    LegacyShippingAwareCost: legacyShippingAwareCost,
                    ShippingPenaltyFloor: shippingPenaltyFloor,
                    CurrentShippingPenalty: currentShippingPenalty,
                    LegacyShippingPenalty: legacyShippingPenalty,
                    CurrentExtraSellerCount: currentExtraSellerCount,
                    LegacyExtraSellerCount: legacyExtraSellerCount,
                    Reason: $"legacy-cheaper{reasonSuffix}");
            }

            if (currentCost + CostEpsilon < legacyCost)
            {
                return new LegacyFallbackOverrideResult(
                    Applied: false,
                    SelectedSellerNames: currentSelection.ToArray(),
                    CurrentCost: currentCost,
                    LegacyCost: legacyCost,
                    CurrentShippingAwareCost: currentShippingAwareCost,
                    LegacyShippingAwareCost: legacyShippingAwareCost,
                    ShippingPenaltyFloor: shippingPenaltyFloor,
                    CurrentShippingPenalty: currentShippingPenalty,
                    LegacyShippingPenalty: legacyShippingPenalty,
                    CurrentExtraSellerCount: currentExtraSellerCount,
                    LegacyExtraSellerCount: legacyExtraSellerCount,
                    Reason: $"legacy-not-cheaper{reasonSuffix}");
            }

            if (legacyExtraSellerCount < currentExtraSellerCount)
            {
                return new LegacyFallbackOverrideResult(
                    Applied: true,
                    SelectedSellerNames: legacySelection.ToArray(),
                    CurrentCost: currentCost,
                    LegacyCost: legacyCost,
                    CurrentShippingAwareCost: currentShippingAwareCost,
                    LegacyShippingAwareCost: legacyShippingAwareCost,
                    ShippingPenaltyFloor: shippingPenaltyFloor,
                    CurrentShippingPenalty: currentShippingPenalty,
                    LegacyShippingPenalty: legacyShippingPenalty,
                    CurrentExtraSellerCount: currentExtraSellerCount,
                    LegacyExtraSellerCount: legacyExtraSellerCount,
                    Reason: $"legacy-fewer-extra-shippings{reasonSuffix}");
            }

            return new LegacyFallbackOverrideResult(
                Applied: false,
                SelectedSellerNames: currentSelection.ToArray(),
                CurrentCost: currentCost,
                LegacyCost: legacyCost,
                CurrentShippingAwareCost: currentShippingAwareCost,
                LegacyShippingAwareCost: legacyShippingAwareCost,
                ShippingPenaltyFloor: shippingPenaltyFloor,
                CurrentShippingPenalty: currentShippingPenalty,
                LegacyShippingPenalty: legacyShippingPenalty,
                CurrentExtraSellerCount: currentExtraSellerCount,
                LegacyExtraSellerCount: legacyExtraSellerCount,
                Reason: $"legacy-tie-keep-current{reasonSuffix}");
        }
        catch (Exception exception)
        {
            return new LegacyFallbackOverrideResult(
                Applied: false,
                SelectedSellerNames: currentSelection.ToArray(),
                CurrentCost: currentCost,
                LegacyCost: InfiniteCost,
                CurrentShippingAwareCost: currentShippingAwareCost,
                LegacyShippingAwareCost: InfiniteCost,
                ShippingPenaltyFloor: shippingPenaltyFloor,
                CurrentShippingPenalty: currentShippingPenalty,
                LegacyShippingPenalty: 0m,
                CurrentExtraSellerCount: currentExtraSellerCount,
                LegacyExtraSellerCount: 0,
                Reason: $"legacy-error:{exception.GetType().Name}");
        }
    }

    private static HashSet<string> RunLegacyFallbackSelection(
        CardMarketSnapshot legacySnapshot,
        IReadOnlyDictionary<string, double> fixedCostBySellerName,
        RuntimeSettings settings,
        int candidatePoolMin,
        int candidatePoolMax,
        int solverTimeBudgetMinutes)
    {
        candidatePoolMax = Math.Max(1, candidatePoolMax);
        candidatePoolMin = Math.Clamp(candidatePoolMin, 1, candidatePoolMax);

        var legacySolver = new BestSellerCalculator();
        var legacyResult = legacySolver.Calculate(
            legacySnapshot,
            fixedCostBySellerName: fixedCostBySellerName,
            solverOptions: new SolverOptions
            {
                CandidateTopCheapestPerCard = settings.CandidateTopCheapestPerCard,
                CandidateTopEffectivePerCard = settings.CandidateTopEffectivePerCard,
                CandidatePoolMin = candidatePoolMin,
                CandidatePoolMax = candidatePoolMax,
                BeamWidth = Math.Min(settings.BeamWidth, 260),
                BeamAlpha = (double)settings.BeamAlpha,
                BeamBeta = (double)settings.BeamBeta,
                ExactMaxK = settings.ExactMaxK,
                EnableFinalCostRefine = settings.EnableFinalCostRefine,
                SolverTimeBudgetMinutes = solverTimeBudgetMinutes
            });

        return legacyResult.SelectedSellers
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> RefineLegacyFallbackOverrideSelection(
        PurgedScopeSnapshot snapshot,
        IReadOnlySet<string> preselectedSellerNames,
        IReadOnlyCollection<string> candidateUniverseSellerNames,
        IReadOnlySet<string> initialSelection,
        decimal shippingPenaltyFloor)
    {
        if (!TryEvaluateLegacyFallbackOverrideSelection(
            snapshot,
            preselectedSellerNames,
            initialSelection,
            shippingPenaltyFloor,
            out var bestCost,
            out var bestShippingAwareCost))
        {
            return initialSelection.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var bestSelection = initialSelection.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = candidateUniverseSellerNames
            .Where(sellerName => !string.IsNullOrWhiteSpace(sellerName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var improved = false;

            foreach (var removeSellerName in bestSelection
                .OrderBy(sellerName => sellerName, StringComparer.OrdinalIgnoreCase))
            {
                var candidateSelection = bestSelection
                    .Where(sellerName => !string.Equals(sellerName, removeSellerName, StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!TryEvaluateLegacyFallbackOverrideSelection(
                    snapshot,
                    preselectedSellerNames,
                    candidateSelection,
                    shippingPenaltyFloor,
                    out var candidateCost,
                    out var candidateShippingAwareCost))
                {
                    continue;
                }

                if (!IsBetterLegacyFallbackOverrideSelection(
                    candidateSelection,
                    candidateCost,
                    candidateShippingAwareCost,
                    bestSelection,
                    bestCost,
                    bestShippingAwareCost))
                {
                    continue;
                }

                bestSelection = candidateSelection;
                bestCost = candidateCost;
                bestShippingAwareCost = candidateShippingAwareCost;
                improved = true;
            }

            foreach (var addSellerName in candidates)
            {
                if (bestSelection.Contains(addSellerName))
                {
                    continue;
                }

                var candidateSelection = bestSelection
                    .Append(addSellerName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!TryEvaluateLegacyFallbackOverrideSelection(
                    snapshot,
                    preselectedSellerNames,
                    candidateSelection,
                    shippingPenaltyFloor,
                    out var candidateCost,
                    out var candidateShippingAwareCost))
                {
                    continue;
                }

                if (!IsBetterLegacyFallbackOverrideSelection(
                    candidateSelection,
                    candidateCost,
                    candidateShippingAwareCost,
                    bestSelection,
                    bestCost,
                    bestShippingAwareCost))
                {
                    continue;
                }

                bestSelection = candidateSelection;
                bestCost = candidateCost;
                bestShippingAwareCost = candidateShippingAwareCost;
                improved = true;
            }

            foreach (var removeSellerName in bestSelection
                .OrderBy(sellerName => sellerName, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var addSellerName in candidates)
                {
                    if (bestSelection.Contains(addSellerName)
                        || string.Equals(removeSellerName, addSellerName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var candidateSelection = bestSelection
                        .Where(sellerName => !string.Equals(sellerName, removeSellerName, StringComparison.OrdinalIgnoreCase))
                        .Append(addSellerName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!TryEvaluateLegacyFallbackOverrideSelection(
                        snapshot,
                        preselectedSellerNames,
                        candidateSelection,
                        shippingPenaltyFloor,
                        out var candidateCost,
                        out var candidateShippingAwareCost))
                    {
                        continue;
                    }

                    if (!IsBetterLegacyFallbackOverrideSelection(
                        candidateSelection,
                        candidateCost,
                        candidateShippingAwareCost,
                        bestSelection,
                        bestCost,
                        bestShippingAwareCost))
                    {
                        continue;
                    }

                    bestSelection = candidateSelection;
                    bestCost = candidateCost;
                    bestShippingAwareCost = candidateShippingAwareCost;
                    improved = true;
                }
            }

            if (!improved)
            {
                break;
            }
        }

        return bestSelection;
    }

    private static bool TryEvaluateLegacyFallbackOverrideSelection(
        PurgedScopeSnapshot snapshot,
        IReadOnlySet<string> preselectedSellerNames,
        IReadOnlyCollection<string> selectedSellerNames,
        decimal shippingPenaltyFloor,
        out decimal scopedCost,
        out decimal shippingAwareCost)
    {
        scopedCost = InfiniteCost;
        shippingAwareCost = InfiniteCost;

        var effectiveSelection = selectedSellerNames
            .Concat(preselectedSellerNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!TryCalculateScopedSelectionObjectiveCost(
            snapshot,
            effectiveSelection,
            out scopedCost))
        {
            return false;
        }

        var shippingPenalty = CalculateLegacyFallbackShippingPenalty(
            selectedSellerNames,
            preselectedSellerNames,
            snapshot.FixedCostBySellerName,
            shippingPenaltyFloor);
        shippingAwareCost = AddCost(scopedCost, shippingPenalty);
        return true;
    }

    private static bool IsBetterLegacyFallbackOverrideSelection(
        IReadOnlyCollection<string> candidateSelection,
        decimal candidateCost,
        decimal candidateShippingAwareCost,
        IReadOnlyCollection<string> incumbentSelection,
        decimal incumbentCost,
        decimal incumbentShippingAwareCost)
    {
        if (candidateShippingAwareCost + CostEpsilon < incumbentShippingAwareCost)
        {
            return true;
        }

        if (incumbentShippingAwareCost + CostEpsilon < candidateShippingAwareCost)
        {
            return false;
        }

        if (candidateCost + CostEpsilon < incumbentCost)
        {
            return true;
        }

        if (incumbentCost + CostEpsilon < candidateCost)
        {
            return false;
        }

        var candidateOrdered = candidateSelection
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(sellerName => sellerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incumbentOrdered = incumbentSelection
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(sellerName => sellerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lexicalCompare = CompareLexicographically(candidateOrdered, incumbentOrdered);
        if (lexicalCompare != 0)
        {
            return lexicalCompare < 0;
        }

        return candidateOrdered.Length < incumbentOrdered.Length;
    }

    private static int CompareLexicographically(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var compareLength = Math.Min(left.Count, right.Count);
        for (var index = 0; index < compareLength; index++)
        {
            var compare = StringComparer.OrdinalIgnoreCase.Compare(left[index], right[index]);
            if (compare != 0)
            {
                return compare;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static int CountLegacyFallbackExtraSellers(
        IReadOnlyCollection<string> selectedSellerNames,
        IReadOnlySet<string> preselectedSellerNames)
    {
        if (selectedSellerNames.Count == 0)
        {
            return 0;
        }

        return selectedSellerNames
            .Where(sellerName => !string.IsNullOrWhiteSpace(sellerName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(sellerName => !preselectedSellerNames.Contains(sellerName));
    }

    private static decimal CalculateLegacyFallbackShippingPenalty(
        IReadOnlyCollection<string> selectedSellerNames,
        IReadOnlySet<string> preselectedSellerNames,
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName,
        decimal shippingPenaltyFloor)
    {
        var totalPenalty = 0m;

        foreach (var sellerName in selectedSellerNames
            .Where(sellerName => !string.IsNullOrWhiteSpace(sellerName))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (preselectedSellerNames.Contains(sellerName))
            {
                continue;
            }

            var fixedCost = fixedCostBySellerName.GetValueOrDefault(sellerName, 0m);
            totalPenalty = AddCost(totalPenalty, fixedCost > 0m ? fixedCost : shippingPenaltyFloor);
        }

        return totalPenalty;
    }

    private static decimal ResolveLegacyFallbackShippingPenaltyFloor(
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName)
    {
        var minimumPositiveFixedCost = fixedCostBySellerName
            .Values
            .Where(cost => cost > 0m)
            .DefaultIfEmpty(0m)
            .Min();

        return minimumPositiveFixedCost;
    }

    private static bool TryCalculateScopedSelectionObjectiveCost(
        PurgedScopeSnapshot snapshot,
        IReadOnlyCollection<string> selectedSellerNames,
        out decimal totalCost)
    {
        totalCost = InfiniteCost;
        if (selectedSellerNames.Count == 0)
        {
            return false;
        }

        var selectedSellerSet = selectedSellerNames
            .Where(sellerName => !string.IsNullOrWhiteSpace(sellerName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedSellerSet.Count == 0)
        {
            return false;
        }

        var cardsTotalCost = 0m;
        foreach (var card in snapshot.ScopedMarketData)
        {
            var requiredQuantity = ResolveRemainingRequiredQuantity(
                ResolveCardKey(card.Target),
                card.Target.DesiredQuantity,
                snapshot.RemainingRequiredByCardKey);
            if (requiredQuantity <= 0)
            {
                continue;
            }

            var coveredQuantity = 0;
            foreach (var offer in card.Offers
                .Where(offer => selectedSellerSet.Contains(offer.SellerName))
                .OrderBy(offer => offer.Price)
                .ThenBy(offer => offer.SellerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(offer => offer.ProductUrl, StringComparer.OrdinalIgnoreCase)
                .ThenBy(offer => offer.CardName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(offer => offer.SetName, StringComparer.OrdinalIgnoreCase))
            {
                if (coveredQuantity >= requiredQuantity)
                {
                    break;
                }

                var take = Math.Min(offer.AvailableQuantity, requiredQuantity - coveredQuantity);
                if (take <= 0)
                {
                    continue;
                }

                cardsTotalCost = AddCost(cardsTotalCost, offer.Price * take);
                coveredQuantity += take;
            }

            if (coveredQuantity < requiredQuantity)
            {
                totalCost = InfiniteCost;
                return false;
            }
        }

        var shippingTotal = selectedSellerSet
            .Sum(sellerName => snapshot.FixedCostBySellerName.GetValueOrDefault(sellerName, 0m));
        totalCost = AddCost(cardsTotalCost, shippingTotal);
        return true;
    }

    private static CardMarketSnapshot BuildLegacySnapshot(
        IReadOnlyList<MarketCardData> purgedMarketData,
        IReadOnlyList<int> requiredByCard,
        IReadOnlySet<string> allowedSellerNames)
    {
        var cards = new List<CardSellersSnapshot>(purgedMarketData.Count);

        for (var cardIndex = 0; cardIndex < purgedMarketData.Count; cardIndex++)
        {
            var card = purgedMarketData[cardIndex];
            var requestedQuantity = cardIndex < requiredByCard.Count
                ? Math.Max(0, requiredByCard[cardIndex])
                : Math.Max(0, card.Target.DesiredQuantity);
            if (requestedQuantity <= 0)
            {
                continue;
            }

            var offers = card.Offers
                .Where(offer => offer.AvailableQuantity > 0)
                .Where(offer => allowedSellerNames.Count == 0 || allowedSellerNames.Contains(offer.SellerName))
                .Select(offer => new CardOffer(
                    SellerName: offer.SellerName,
                    SellerProfileUrl: string.Empty,
                    CardName: ResolveCardKey(card.Target),
                    SourceUrl: offer.ProductUrl,
                    Price: (double)offer.Price,
                    AvailableQuantity: offer.AvailableQuantity,
                    SellerCountry: offer.Country,
                    Language: string.Empty,
                    Condition: string.Empty))
                .ToList();

            var firstPrice = offers.Count == 0 ? 0d : offers.Min(offer => offer.Price);
            var maxPrice = offers.Count == 0 ? 0d : offers.Max(offer => offer.Price);

            cards.Add(new CardSellersSnapshot(
                Request: new CardRequest(ResolveCardKey(card.Target), requestedQuantity),
                Offers: offers,
                FirstPrice: firstPrice,
                MaxPrice: maxPrice));
        }

        return new CardMarketSnapshot(cards);
    }

    private static int[] BuildRemainingRequiredByCard(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlyCollection<string> selectedSellerNames,
        IReadOnlySet<int> knownUncoveredCardIndices)
    {
        var remaining = requiredByCard.ToArray();
        foreach (var uncoveredCardIndex in knownUncoveredCardIndices)
        {
            if (uncoveredCardIndex >= 0 && uncoveredCardIndex < remaining.Length)
            {
                remaining[uncoveredCardIndex] = 0;
            }
        }

        if (selectedSellerNames.Count == 0)
        {
            return remaining;
        }

        for (var sellerIndex = 0; sellerIndex < orderedSellers.Count; sellerIndex++)
        {
            var seller = orderedSellers[sellerIndex];
            if (!selectedSellerNames.Contains(seller.SellerName))
            {
                continue;
            }

            foreach (var cardIndex in seller.ActiveCards)
            {
                remaining[cardIndex] = Math.Max(0, remaining[cardIndex] - seller.QtyByCard[cardIndex]);
            }
        }

        return remaining;
    }

    private static AssignmentValidationResult ValidateAssignmentsAndRepairResidual(
        IReadOnlyList<MarketCardData> marketData,
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName,
        IReadOnlyCollection<string> selectedSellerNames,
        IReadOnlyList<PurchaseAssignment> initialAssignments,
        IReadOnlyCollection<string> initialUncoveredCards)
    {
        var effectiveSelectedSellerNames = selectedSellerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = initialAssignments.ToList();
        var uncoveredCardKeys = initialUncoveredCards.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (uncoveredCardKeys.Count > 0)
        {
            var remainingByCardKey = BuildRemainingByCardKey(marketData, assignments);
            ApplyResidualFallback(
                marketData,
                fixedCostBySellerName,
                remainingByCardKey,
                effectiveSelectedSellerNames);

            assignments = BuildAssignments(
                marketData,
                effectiveSelectedSellerNames,
                out var repairedUncoveredCards,
                out var repairedCardsTotalPrice);

            return new AssignmentValidationResult(
                effectiveSelectedSellerNames,
                assignments,
                repairedCardsTotalPrice,
                repairedUncoveredCards.ToHashSet(StringComparer.OrdinalIgnoreCase),
                effectiveSelectedSellerNames.Except(selectedSellerNames, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        return new AssignmentValidationResult(
            effectiveSelectedSellerNames,
            assignments,
            initialAssignments.Sum(assignment => assignment.TotalPrice),
            uncoveredCardKeys,
            []);
    }

    private static Dictionary<string, int> BuildRemainingByCardKey(
        IReadOnlyList<MarketCardData> marketData,
        IReadOnlyList<PurchaseAssignment> assignments)
    {
        var remainingByCardKey = marketData.ToDictionary(
            card => ResolveCardKey(card.Target),
            card => Math.Max(0, card.Target.DesiredQuantity),
            StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in assignments)
        {
            var matchingCard = marketData.FirstOrDefault(card =>
                string.Equals(card.Target.ProductUrl, assignment.ProductUrl, StringComparison.OrdinalIgnoreCase)
                || string.Equals(card.Target.CardName, assignment.CardName, StringComparison.OrdinalIgnoreCase));

            if (matchingCard is null)
            {
                continue;
            }

            var cardKey = ResolveCardKey(matchingCard.Target);
            remainingByCardKey[cardKey] = Math.Max(0, remainingByCardKey.GetValueOrDefault(cardKey) - assignment.Quantity);
        }

        return remainingByCardKey;
    }

    private static void ApplyResidualFallback(
        IReadOnlyList<MarketCardData> marketData,
        IReadOnlyDictionary<string, decimal> fixedCostBySellerName,
        Dictionary<string, int> remainingByCardKey,
        HashSet<string> selectedSellerNames)
    {
        while (remainingByCardKey.Values.Any(quantity => quantity > 0))
        {
            var canonicalBuild = BuildCanonicalSellersWithMetrics(
                marketData,
                marketData
                    .Select(card => remainingByCardKey.GetValueOrDefault(ResolveCardKey(card.Target)))
                    .ToArray(),
                fixedCostBySellerName);

            var bestPackSeller = canonicalBuild.Sellers
                .Where(seller => !selectedSellerNames.Contains(seller.SellerName) && seller.ActiveCardCount >= 2)
                .Select(seller =>
                {
                    var coveredCards = 0;
                    var coveredUnits = 0;
                    var oneUnitCost = seller.FixedCost;

                    foreach (var cardIndex in seller.ActiveCards)
                    {
                        var requiredQty = remainingByCardKey.GetValueOrDefault(ResolveCardKey(marketData[cardIndex].Target));
                        if (requiredQty <= 0)
                        {
                            continue;
                        }

                        coveredCards++;
                        coveredUnits += Math.Min(requiredQty, seller.QtyByCard[cardIndex]);
                        var profile = seller.CardProfiles[cardIndex];
                        if (profile is not null && profile.QtyUsable > 0)
                        {
                            oneUnitCost = AddCost(oneUnitCost, profile.PrefixCosts[1]);
                        }
                    }

                    return new FallbackPackCandidate(
                        seller.SellerName,
                        coveredCards,
                        coveredUnits,
                        oneUnitCost,
                        seller.OriginalOrder);
                })
                .OrderByDescending(candidate => candidate.CoveredCards)
                .ThenByDescending(candidate => candidate.CoveredUnits)
                .ThenBy(candidate => candidate.OneUnitPackCost)
                .ThenBy(candidate => candidate.SellerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.OriginalOrder)
                .FirstOrDefault();

            if (bestPackSeller is null || bestPackSeller.CoveredCards < 2)
            {
                break;
            }

            selectedSellerNames.Add(bestPackSeller.SellerName);
            foreach (var card in marketData)
            {
                var cardKey = ResolveCardKey(card.Target);
                var sellerQty = card.Offers
                    .Where(offer => string.Equals(offer.SellerName, bestPackSeller.SellerName, StringComparison.OrdinalIgnoreCase))
                    .Sum(offer => offer.AvailableQuantity);
                remainingByCardKey[cardKey] = Math.Max(0, remainingByCardKey.GetValueOrDefault(cardKey) - sellerQty);
            }
        }

        foreach (var card in marketData)
        {
            var cardKey = ResolveCardKey(card.Target);
            var remainingQty = remainingByCardKey.GetValueOrDefault(cardKey);
            if (remainingQty <= 0)
            {
                continue;
            }

            var orderedOffers = card.Offers
                .Where(offer => offer.AvailableQuantity > 0)
                .Where(offer => !selectedSellerNames.Contains(offer.SellerName))
                .OrderBy(offer => offer.Price)
                .ThenByDescending(offer => offer.AvailableQuantity)
                .ThenBy(offer => offer.SellerName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var offer in orderedOffers)
            {
                if (remainingQty <= 0)
                {
                    break;
                }

                selectedSellerNames.Add(offer.SellerName);
                remainingQty -= offer.AvailableQuantity;
            }

            remainingByCardKey[cardKey] = Math.Max(0, remainingQty);
        }
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
        return RefineSelectionForFixedSellerCountOnOrderedCanonical(
            orderedSellers,
            requiredByCard,
            selectedSellerNames,
            effectiveParallelism);
    }

    private static HashSet<string> RefineSelectionForFixedSellerCountOnOrderedCanonical(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlyCollection<string> selectedSellerNames,
        int effectiveParallelism,
        SearchPrecomputationContext? searchContext = null)
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

        if (orderedSellers.Count < targetSellerCount)
        {
            return selectedSellerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var effectiveSearchContext = searchContext is not null && searchContext.SellerCount == orderedSellers.Count && searchContext.CardCount == cardCount
            ? searchContext
            : BuildSearchPrecomputationContext(orderedSellers, requiredByCard, cardCount);
        EnsureSearchContextHasSuffixMinimumCosts(effectiveSearchContext, orderedSellers, requiredByCard);

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
            effectiveSearchContext,
            effectiveParallelism,
            initialUpperBoundCost,
            initialSelection).BestSelection;

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

    private static SearchPrecomputationContext BuildSearchPrecomputationContext(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount)
    {
        var sellerCardQty = BuildSellerCardQtyMatrix(orderedSellers, cardCount);
        var suffixCoverage = BuildSuffixCoverageMatrix(sellerCardQty, orderedSellers.Count, cardCount);
        var fixedCostLowerBounds = BuildFixedCostLowerBounds(orderedSellers);

        return new SearchPrecomputationContext(
            sellerCardQty,
            suffixCoverage,
            fixedCostLowerBounds,
            orderedSellers.Count,
            cardCount);
    }

    private static void EnsureSearchContextHasSuffixMinimumCosts(
        SearchPrecomputationContext searchContext,
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard)
    {
        if (searchContext.SuffixMinCosts is not null)
        {
            return;
        }

        searchContext.SuffixMinCosts = BuildSuffixMinimumCosts(
            orderedSellers,
            requiredByCard,
            searchContext.SellerCount,
            searchContext.CardCount);
        searchContext.MinimumCardCostLowerBound = CalculateMinimumCardCostLowerBound(
            searchContext.SuffixMinCosts,
            requiredByCard,
            searchContext.CardCount);
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

    private static decimal[] BuildFixedCostLowerBounds(
        IReadOnlyList<CanonicalSeller> orderedSellers)
    {
        var orderedFixedCosts = orderedSellers
            .Select(seller => seller.FixedCost)
            .OrderBy(cost => cost)
            .ToArray();
        var lowerBounds = new decimal[orderedFixedCosts.Length + 1];

        for (var sellerCount = 1; sellerCount <= orderedFixedCosts.Length; sellerCount++)
        {
            lowerBounds[sellerCount] = AddCost(lowerBounds[sellerCount - 1], orderedFixedCosts[sellerCount - 1]);
        }

        return lowerBounds;
    }

    private static decimal CalculateMinimumCardCostLowerBound(
        decimal[,,] suffixMinCosts,
        int[] requiredByCard,
        int cardCount)
    {
        var totalLowerBound = 0m;

        for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            var requiredQty = requiredByCard[cardIndex];
            if (requiredQty <= 0)
            {
                continue;
            }

            var bound = suffixMinCosts[0, cardIndex, requiredQty];
            if (IsInfinite(bound))
            {
                return InfiniteCost;
            }

            totalLowerBound = AddCost(totalLowerBound, bound);
        }

        return totalLowerBound;
    }

    private static decimal CalculateLowerBoundForTargetSellerCount(
        SearchPrecomputationContext searchContext,
        int targetSellerCount)
    {
        if (targetSellerCount < 0 || targetSellerCount >= searchContext.FixedCostLowerBoundsBySellerCount.Length)
        {
            return InfiniteCost;
        }

        if (IsInfinite(searchContext.MinimumCardCostLowerBound))
        {
            return InfiniteCost;
        }

        return AddCost(
            searchContext.MinimumCardCostLowerBound,
            searchContext.FixedCostLowerBoundsBySellerCount[targetSellerCount]);
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

    private static FixedKSearchResult FindBestSelectionForFixedK(
        int targetSellerCount,
        int[] requiredByCard,
        IReadOnlyList<CanonicalSeller> orderedSellers,
        SearchPrecomputationContext searchContext,
        int effectiveParallelism,
        decimal initialUpperBoundCost,
        IReadOnlyList<int>? initialSelection)
    {
        var sellerCount = searchContext.SellerCount;
        var cardCount = searchContext.CardCount;

        if (targetSellerCount < 0 || targetSellerCount > sellerCount)
        {
            return new FixedKSearchResult(null, 0, 0);
        }

        var suffixMinCosts = searchContext.SuffixMinCosts
            ?? throw new InvalidOperationException("The exact-search context must include suffix minimum costs.");
        var initialBest = initialSelection is null || IsInfinite(initialUpperBoundCost)
            ? null
            : new SelectionResult(initialSelection.ToArray(), initialUpperBoundCost);
        var sharedIncumbent = new SharedSearchIncumbent(initialBest);

        if (targetSellerCount == 0)
        {
            var state = new CostSearchState(
                targetSellerCount,
                requiredByCard,
                orderedSellers,
                searchContext.SellerCardQty,
                searchContext.SuffixCoverage,
                suffixMinCosts,
                sellerCount,
                cardCount,
                Array.Empty<int>(),
                0,
                initialUpperBoundCost,
                initialSelection,
                sharedIncumbent);

            state.Search();
            return new FixedKSearchResult(sharedIncumbent.Snapshot, 1, sharedIncumbent.UpdateCount);
        }

        if (effectiveParallelism <= 1 || sellerCount - targetSellerCount + 1 <= 1)
        {
            var state = new CostSearchState(
                targetSellerCount,
                requiredByCard,
                orderedSellers,
                searchContext.SellerCardQty,
                searchContext.SuffixCoverage,
                suffixMinCosts,
                sellerCount,
                cardCount,
                Array.Empty<int>(),
                0,
                initialUpperBoundCost,
                initialSelection,
                sharedIncumbent);

            state.Search();
            return new FixedKSearchResult(sharedIncumbent.Snapshot, 1, sharedIncumbent.UpdateCount);
        }

        var partitions = BuildWeightedParallelPartitions(
            sellerCount,
            targetSellerCount,
            effectiveParallelism);

        Parallel.ForEach(
            partitions,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = effectiveParallelism
            },
            partition =>
            {
                for (var firstSellerIndex = partition.StartFirstSellerIndex; firstSellerIndex < partition.EndFirstSellerIndexExclusive; firstSellerIndex++)
                {
                    var initialSelected = new[] { firstSellerIndex };
                    var state = new CostSearchState(
                        targetSellerCount,
                        requiredByCard,
                        orderedSellers,
                        searchContext.SellerCardQty,
                        searchContext.SuffixCoverage,
                        suffixMinCosts,
                        sellerCount,
                        cardCount,
                        initialSelected,
                        firstSellerIndex + 1,
                        initialUpperBoundCost,
                        initialSelection,
                        sharedIncumbent);

                    state.Search();
                }
            });

        return new FixedKSearchResult(sharedIncumbent.Snapshot, partitions.Count, sharedIncumbent.UpdateCount);
    }

    private static List<ParallelSearchPartition> BuildWeightedParallelPartitions(
        int sellerCount,
        int targetSellerCount,
        int effectiveParallelism)
    {
        var partitionableFirstSellerCount = sellerCount - targetSellerCount + 1;
        if (partitionableFirstSellerCount <= 0)
        {
            return [];
        }

        if (partitionableFirstSellerCount == 1)
        {
            return [new ParallelSearchPartition(0, 1, 1d)];
        }

        var targetRemaining = targetSellerCount - 1;
        var firstSellerWorkItems = new List<(int FirstSellerIndex, double Work)>(partitionableFirstSellerCount);
        var totalWork = 0d;

        for (var firstSellerIndex = 0; firstSellerIndex < partitionableFirstSellerCount; firstSellerIndex++)
        {
            var work = targetRemaining <= 0
                ? 1d
                : CalculateCombinationCount(sellerCount - firstSellerIndex - 1, targetRemaining);
            if (double.IsNaN(work) || work <= 0d)
            {
                work = 1d;
            }

            firstSellerWorkItems.Add((firstSellerIndex, work));
            totalWork += work;
        }

        if (totalWork <= 0d || double.IsNaN(totalWork))
        {
            return [new ParallelSearchPartition(0, partitionableFirstSellerCount, partitionableFirstSellerCount)];
        }

        var targetPartitionCount = Math.Min(
            partitionableFirstSellerCount,
            Math.Max(1, effectiveParallelism * 2));
        var targetWorkPerPartition = totalWork / targetPartitionCount;
        var partitions = new List<ParallelSearchPartition>(targetPartitionCount);
        var currentStart = firstSellerWorkItems[0].FirstSellerIndex;
        var currentWork = 0d;

        for (var itemIndex = 0; itemIndex < firstSellerWorkItems.Count; itemIndex++)
        {
            var item = firstSellerWorkItems[itemIndex];
            currentWork += item.Work;
            var isLastItem = itemIndex == firstSellerWorkItems.Count - 1;
            var shouldClosePartition = isLastItem
                || (currentWork >= targetWorkPerPartition && partitions.Count + 1 < targetPartitionCount);

            if (!shouldClosePartition)
            {
                continue;
            }

            partitions.Add(new ParallelSearchPartition(
                currentStart,
                item.FirstSellerIndex + 1,
                currentWork));

            if (!isLastItem)
            {
                currentStart = firstSellerWorkItems[itemIndex + 1].FirstSellerIndex;
                currentWork = 0d;
            }
        }

        return partitions;
    }

    private static double CalculateCombinationCount(int n, int r)
    {
        if (r < 0 || r > n)
        {
            return 0d;
        }

        if (r == 0 || r == n)
        {
            return 1d;
        }

        var k = Math.Min(r, n - r);
        var result = 1d;

        for (var index = 1; index <= k; index++)
        {
            result *= n - (k - index);
            result /= index;
        }

        return result;
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

    private static BruteForceSelectionResult FindBestSelectionByObjective(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        int cardCount)
    {
        var sellerCount = orderedSellers.Count;
        BruteForceSelectionResult? best = null;
        var evaluated = 0;
        var subsetCount = 1 << sellerCount;

        for (var mask = 1; mask < subsetCount; mask++)
        {
            var selectedIndices = new List<int>(sellerCount);
            for (var sellerIndex = 0; sellerIndex < sellerCount; sellerIndex++)
            {
                if ((mask & (1 << sellerIndex)) != 0)
                {
                    selectedIndices.Add(sellerIndex);
                }
            }

            var objective = EvaluatePartialSelectionObjective(
                orderedSellers,
                requiredByCard,
                cardCount,
                selectedIndices);
            evaluated++;

            var candidate = new BruteForceSelectionResult(
                objective.SelectedOrderedSellerIndices,
                objective.UncoveredCardIndices,
                objective.TotalCost,
                evaluated);

            if (best is null || CompareBruteForceObjective(candidate, best.Value) < 0)
            {
                best = candidate;
            }
        }

        return best ?? new BruteForceSelectionResult([], [], 0m, 0);
    }

    private static int CompareBruteForceObjective(
        BruteForceSelectionResult left,
        BruteForceSelectionResult right)
    {
        var uncoveredCountComparison = left.UncoveredCardIndices.Count.CompareTo(right.UncoveredCardIndices.Count);
        if (uncoveredCountComparison != 0)
        {
            return uncoveredCountComparison;
        }

        var uncoveredIndicesComparison = CompareSelectionsLexicographically(
            left.UncoveredCardIndices,
            right.UncoveredCardIndices);
        if (uncoveredIndicesComparison != 0)
        {
            return uncoveredIndicesComparison;
        }

        if (left.TotalCost + CostEpsilon < right.TotalCost)
        {
            return -1;
        }

        if (right.TotalCost + CostEpsilon < left.TotalCost)
        {
            return 1;
        }

        return CompareSelectionsLexicographically(
            left.SelectedOrderedSellerIndices,
            right.SelectedOrderedSellerIndices);
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

    private static HashSet<string> PruneRedundantFullCoverageSelection(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlyCollection<string> selectedSellerNames)
    {
        var selectedIndices = orderedSellers
            .Select((seller, index) => new { seller.SellerName, Index = index })
            .Where(item => selectedSellerNames.Contains(item.SellerName))
            .Select(item => item.Index)
            .OrderBy(index => index)
            .ToList();

        if (selectedIndices.Count <= 1)
        {
            return selectedSellerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var currentBest = new SelectionResult(
            selectedIndices,
            CalculateExactCostForSelection(
                orderedSellers,
                requiredByCard,
                requiredByCard.Length,
                selectedIndices));

        while (true)
        {
            SelectionResult? bestPruned = null;

            for (var selectedOffset = 0; selectedOffset < currentBest.SelectedOrderedSellerIndices.Count; selectedOffset++)
            {
                var candidateIndices = currentBest.SelectedOrderedSellerIndices
                    .Where((_, index) => index != selectedOffset)
                    .ToList();

                if (candidateIndices.Count == 0
                    || !IsSelectionFullyCovered(orderedSellers, requiredByCard, candidateIndices))
                {
                    continue;
                }

                var candidateCost = CalculateExactCostForSelection(
                    orderedSellers,
                    requiredByCard,
                    requiredByCard.Length,
                    candidateIndices);
                if (IsInfinite(candidateCost))
                {
                    continue;
                }

                var candidate = new SelectionResult(candidateIndices, candidateCost);
                if (!IsBetterFullCoverageCandidate(candidate, currentBest, orderedSellers))
                {
                    continue;
                }

                if (bestPruned is null || IsBetterFullCoverageCandidate(candidate, bestPruned, orderedSellers))
                {
                    bestPruned = candidate;
                }
            }

            if (bestPruned is null)
            {
                break;
            }

            currentBest = bestPruned;
        }

        return currentBest.SelectedOrderedSellerIndices
            .Select(index => orderedSellers[index].SellerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSelectionFullyCovered(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlyList<int> selectedOrderedSellerIndices)
    {
        var coveredByCard = new int[requiredByCard.Length];

        for (var selectedOffset = 0; selectedOffset < selectedOrderedSellerIndices.Count; selectedOffset++)
        {
            var seller = orderedSellers[selectedOrderedSellerIndices[selectedOffset]];
            foreach (var cardIndex in seller.ActiveCards)
            {
                coveredByCard[cardIndex] += seller.QtyByCard[cardIndex];
            }
        }

        return IsFullyCovered(requiredByCard, coveredByCard, requiredByCard.Length);
    }

    private static bool IsSelectionFullyCoveredByNames(
        IReadOnlyList<CanonicalSeller> orderedSellers,
        int[] requiredByCard,
        IReadOnlyCollection<string> selectedSellerNames)
    {
        var selectedOrderedSellerIndices = orderedSellers
            .Select((seller, index) => new { seller.SellerName, Index = index })
            .Where(item => selectedSellerNames.Contains(item.SellerName))
            .Select(item => item.Index)
            .ToList();

        return IsSelectionFullyCovered(orderedSellers, requiredByCard, selectedOrderedSellerIndices);
    }

    private static bool IsBetterFullCoverageCandidate(
        SelectionResult candidate,
        SelectionResult currentBest,
        IReadOnlyList<CanonicalSeller> orderedSellers)
    {
        if (candidate.TotalCost + CostEpsilon < currentBest.TotalCost)
        {
            return true;
        }

        if (currentBest.TotalCost + CostEpsilon < candidate.TotalCost)
        {
            return false;
        }

        var candidateSellerNames = candidate.SelectedOrderedSellerIndices
            .Select(index => orderedSellers[index].SellerName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentSellerNames = currentBest.SelectedOrderedSellerIndices
            .Select(index => orderedSellers[index].SellerName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return CompareSelectionsLexicographically(candidateSellerNames, currentSellerNames) < 0;
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

    private static int CompareSelectionsLexicographically(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var count = Math.Min(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            var compare = StringComparer.OrdinalIgnoreCase.Compare(left[index], right[index]);
            if (compare != 0)
            {
                return compare;
            }
        }

        return left.Count.CompareTo(right.Count);
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
        private readonly int _effectiveParallelism;

        public CandidatePoolBuilder(int effectiveParallelism)
        {
            _effectiveParallelism = Math.Max(1, effectiveParallelism);
        }

        public CandidatePoolBuildResult BuildCandidateSellerNames(
            IReadOnlyList<CanonicalSeller> sellers,
            IReadOnlyList<string> cardNames,
            int[] requiredByCard,
            IReadOnlyCollection<string> alwaysKeepSellerNames,
            RuntimeSettings settings)
        {
            var candidateSellerNames = new HashSet<string>(
                alwaysKeepSellerNames,
                StringComparer.OrdinalIgnoreCase);

            if (sellers.Count == 0)
            {
                return new CandidatePoolBuildResult(
                    candidateSellerNames,
                    [],
                    RareProviderCount: 0,
                    FeasibilityRescueCount: 0,
                    CoveragePromotionCount: 0,
                    TopHitPromotionCount: 0);
            }

            var topHitsBySeller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rareProviderSellerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cardEvaluations = new CandidateCardEvaluation[requiredByCard.Length];

            Action<int> evaluateCard = cardIndex =>
            {
                var requiredQty = requiredByCard[cardIndex];
                if (requiredQty <= 0)
                {
                    cardEvaluations[cardIndex] = CandidateCardEvaluation.Empty(cardNames[cardIndex]);
                    return;
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
                    cardEvaluations[cardIndex] = CandidateCardEvaluation.Empty(cardNames[cardIndex]);
                    return;
                }

                var adaptiveCheapestCap = ResolveAdaptiveTopPerCard(
                    requiredQty,
                    providers.Count,
                    settings.CandidateTopCheapestPerCard);
                var adaptiveEffectiveCap = ResolveAdaptiveTopPerCard(
                    requiredQty,
                    providers.Count,
                    settings.CandidateTopEffectivePerCard);

                var cheapestProviders = providers
                    .OrderBy(provider => provider.UnitPrice)
                    .ThenBy(provider => provider.Seller.SellerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(provider => provider.Seller.OriginalOrder)
                    .Take(adaptiveCheapestCap)
                    .Select(provider => provider.Seller.SellerName)
                    .ToArray();

                var effectiveProviders = providers
                    .OrderBy(provider => provider.EffectiveCost)
                    .ThenBy(provider => provider.UnitPrice)
                    .ThenBy(provider => provider.Seller.SellerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(provider => provider.Seller.OriginalOrder)
                    .Take(adaptiveEffectiveCap)
                    .Select(provider => provider.Seller.SellerName)
                    .ToArray();

                var rareProviders = providers.Count <= 2
                    ? providers.Select(provider => provider.Seller.SellerName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    : [];

                cardEvaluations[cardIndex] = new CandidateCardEvaluation(
                    cardNames[cardIndex],
                    providers.Count,
                    adaptiveCheapestCap,
                    adaptiveEffectiveCap,
                    cheapestProviders,
                    effectiveProviders,
                    rareProviders);
            };

            if (_effectiveParallelism <= 1 || requiredByCard.Length <= 1)
            {
                for (var cardIndex = 0; cardIndex < requiredByCard.Length; cardIndex++)
                {
                    evaluateCard(cardIndex);
                }
            }
            else
            {
                Parallel.For(
                    0,
                    requiredByCard.Length,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _effectiveParallelism
                    },
                    evaluateCard);
            }

            var cardDetails = new List<OptimizationProfileDetail>(cardEvaluations.Length);
            foreach (var evaluation in cardEvaluations.Where(evaluation => evaluation is not null))
            {
                foreach (var sellerName in evaluation!.RareProviders)
                {
                    candidateSellerNames.Add(sellerName);
                    rareProviderSellerNames.Add(sellerName);
                }

                foreach (var sellerName in evaluation.CheapestProviders)
                {
                    candidateSellerNames.Add(sellerName);
                    topHitsBySeller[sellerName] = topHitsBySeller.GetValueOrDefault(sellerName) + 1;
                }

                foreach (var sellerName in evaluation.EffectiveProviders)
                {
                    candidateSellerNames.Add(sellerName);
                    topHitsBySeller[sellerName] = topHitsBySeller.GetValueOrDefault(sellerName) + 1;
                }

                cardDetails.Add(new OptimizationProfileDetail
                {
                    Name = evaluation.CardName,
                    Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["providers"] = evaluation.ProviderCount,
                        ["adaptiveCheapestCap"] = evaluation.AdaptiveCheapestCap,
                        ["adaptiveEffectiveCap"] = evaluation.AdaptiveEffectiveCap,
                        ["rareProviders"] = evaluation.RareProviders.Length,
                        ["cheapestSelected"] = evaluation.CheapestProviders.Length,
                        ["effectiveSelected"] = evaluation.EffectiveProviders.Length
                    }
                });
            }

            var coveragePromotionCount = 0;
            var topHitPromotionCount = 0;

            foreach (var seller in sellers)
            {
                var coveredCards = CountCoveredCards(seller, requiredByCard);
                var coverageUnits = CountCoveredUnits(seller, requiredByCard);
                var topHits = topHitsBySeller.GetValueOrDefault(seller.SellerName);

                if (topHits >= 2)
                {
                    if (candidateSellerNames.Add(seller.SellerName))
                    {
                        topHitPromotionCount++;
                    }
                    continue;
                }

                if (!rareProviderSellerNames.Contains(seller.SellerName)
                    && coveredCards >= 2
                    && coverageUnits >= Math.Max(3, coveredCards + 1))
                {
                    if (candidateSellerNames.Add(seller.SellerName))
                    {
                        coveragePromotionCount++;
                    }
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

            var feasibilityRescueCount = EnsureFeasibleCoverage(
                candidateSellerNames,
                sellers,
                requiredByCard,
                settings.CandidatePoolMax,
                ranked);

            candidateSellerNames = TrimToCapWithFeasibility(
                candidateSellerNames,
                sellers,
                requiredByCard,
                settings.CandidatePoolMax,
                alwaysKeepSellerNames,
                rareProviderSellerNames,
                ranked);

            return new CandidatePoolBuildResult(
                candidateSellerNames,
                cardDetails,
                RareProviderCount: rareProviderSellerNames.Count,
                FeasibilityRescueCount: feasibilityRescueCount,
                CoveragePromotionCount: coveragePromotionCount,
                TopHitPromotionCount: topHitPromotionCount);
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

        private static int EnsureFeasibleCoverage(
            HashSet<string> candidateSellerNames,
            IReadOnlyList<CanonicalSeller> sellers,
            int[] requiredByCard,
            int maxCount,
            IReadOnlyList<RankedSeller> ranked)
        {
            var rescueCount = 0;

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

                    if (candidateSellerNames.Add(missingProvider.Seller.SellerName))
                    {
                        rescueCount++;
                    }
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
                return rescueCount;
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

                candidateSellerNames.Remove(rankedSeller.SellerName);
                if (!HasFeasibleCoverage(candidateSellerNames, sellers, requiredByCard))
                {
                    candidateSellerNames.Add(rankedSeller.SellerName);
                }
            }

            return rescueCount;
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

        private static int CountCoveredUnits(CanonicalSeller seller, int[] requiredByCard)
        {
            var coveredUnits = 0;

            foreach (var cardIndex in seller.ActiveCards)
            {
                if (requiredByCard[cardIndex] <= 0)
                {
                    continue;
                }

                coveredUnits += Math.Min(requiredByCard[cardIndex], seller.QtyByCard[cardIndex]);
            }

            return coveredUnits;
        }

        private static int ResolveAdaptiveTopPerCard(
            int requiredQuantity,
            int providerCount,
            int configuredCap)
        {
            var adaptiveCap = Math.Max(2, (requiredQuantity * 2) + 1);
            return Math.Min(providerCount, Math.Min(configuredCap, adaptiveCap));
        }

        private sealed record CardProvider(
            CanonicalSeller Seller,
            decimal UnitPrice,
            decimal EffectiveCost,
            int UsefulUnits);

        private sealed record CandidateCardEvaluation(
            string CardName,
            int ProviderCount,
            int AdaptiveCheapestCap,
            int AdaptiveEffectiveCap,
            string[] CheapestProviders,
            string[] EffectiveProviders,
            string[] RareProviders)
        {
            public static CandidateCardEvaluation Empty(string cardName)
                => new(cardName, 0, 0, 0, [], [], []);
        }

        public sealed record CandidatePoolBuildResult(
            HashSet<string> CandidateSellerNames,
            IReadOnlyList<OptimizationProfileDetail> CardDetails,
            int RareProviderCount,
            int FeasibilityRescueCount,
            int CoveragePromotionCount,
            int TopHitPromotionCount);

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
                return new ReducedExactSolveResult(
                    false,
                    [],
                    InfiniteCost,
                    null,
                    new ExactSearchMetrics(
                        LowerBoundSellerCount: 0,
                        TargetKsEvaluated: 0,
                        DeadlineHit: false,
                        BestCostUpdates: 0,
                        PartitionCount: 0,
                        IncumbentSource: "none"));
            }

            var sellerCount = _orderedSellers.Count;
            var searchContext = BuildSearchPrecomputationContext(_orderedSellers, _requiredByCard, _cardCount);
            var sellerCardQty = searchContext.SellerCardQty;

            var impossibleCardIndices = FindImpossibleCardIndices(_requiredByCard, sellerCardQty, sellerCount);
            var incumbentSource = incumbent.IsFullCoverage
                ? "beam"
                : incumbent.SelectedOrderedSellerIndices.Count > 0
                    ? "beam-partial"
                    : "none";
            if (impossibleCardIndices.Count > 0)
            {
                return new ReducedExactSolveResult(
                    incumbent.IsFullCoverage,
                    incumbent.SelectedOrderedSellerIndices,
                    incumbent.TotalCost,
                    searchContext,
                    new ExactSearchMetrics(
                        LowerBoundSellerCount: 0,
                        TargetKsEvaluated: 0,
                        DeadlineHit: false,
                        BestCostUpdates: 0,
                        PartitionCount: 0,
                        IncumbentSource: incumbentSource));
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
                    ? new ReducedExactSolveResult(
                        false,
                        incumbent.SelectedOrderedSellerIndices,
                        incumbent.TotalCost,
                        searchContext,
                        new ExactSearchMetrics(
                            lowerBoundSellerCount,
                            TargetKsEvaluated: 0,
                            DeadlineHit: false,
                            BestCostUpdates: 0,
                            PartitionCount: 0,
                            IncumbentSource: incumbentSource))
                    : new ReducedExactSolveResult(
                        true,
                        bestSelection.SelectedOrderedSellerIndices,
                        bestSelection.TotalCost,
                        searchContext,
                        new ExactSearchMetrics(
                            lowerBoundSellerCount,
                            TargetKsEvaluated: 0,
                            DeadlineHit: false,
                            BestCostUpdates: 0,
                            PartitionCount: 0,
                            IncumbentSource: incumbentSource));
            }

            EnsureSearchContextHasSuffixMinimumCosts(searchContext, _orderedSellers, _requiredByCard);
            var deadline = TimeSpan.FromMinutes(_settings.SolverTimeBudgetMinutes);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var targetKsEvaluated = 0;
            var deadlineHit = false;
            var bestCostUpdates = 0;
            var partitionCount = 0;

            for (var targetSellerCount = lowerBoundSellerCount; targetSellerCount <= maxK; targetSellerCount++)
            {
                if (stopwatch.Elapsed >= deadline)
                {
                    deadlineHit = true;
                    break;
                }

                if (bestSelection is not null)
                {
                    var targetLowerBound = CalculateLowerBoundForTargetSellerCount(searchContext, targetSellerCount);
                    if (!IsInfinite(targetLowerBound) && targetLowerBound > bestSelection.TotalCost + CostEpsilon)
                    {
                        break;
                    }
                }

                var initialSelection = bestSelection is not null && bestSelection.SelectedOrderedSellerIndices.Count == targetSellerCount
                    ? bestSelection.SelectedOrderedSellerIndices
                    : null;
                var initialUpperBoundCost = bestSelection?.TotalCost ?? InfiniteCost;

                var searchResult = FindBestSelectionForFixedK(
                    targetSellerCount,
                    _requiredByCard,
                    _orderedSellers,
                    searchContext,
                    _effectiveParallelism,
                    initialUpperBoundCost,
                    initialSelection);
                targetKsEvaluated++;
                partitionCount += searchResult.PartitionCount;
                bestCostUpdates += searchResult.IncumbentUpdates;

                var candidate = searchResult.BestSelection;

                if (candidate is null || IsInfinite(candidate.TotalCost))
                {
                    continue;
                }

                if (IsBetterCandidate(candidate, bestSelection))
                {
                    bestSelection = candidate;
                    incumbentSource = "exact";
                }
            }

            if (bestSelection is not null)
            {
                return new ReducedExactSolveResult(
                    IsFullCoverage: true,
                    SelectedOrderedSellerIndices: bestSelection.SelectedOrderedSellerIndices,
                    TotalCost: bestSelection.TotalCost,
                    SearchContext: searchContext,
                    Metrics: new ExactSearchMetrics(
                        lowerBoundSellerCount,
                        targetKsEvaluated,
                        deadlineHit,
                        bestCostUpdates,
                        partitionCount,
                        incumbentSource));
            }

            return new ReducedExactSolveResult(
                IsFullCoverage: incumbent.IsFullCoverage,
                SelectedOrderedSellerIndices: incumbent.SelectedOrderedSellerIndices,
                TotalCost: incumbent.TotalCost,
                SearchContext: searchContext,
                Metrics: new ExactSearchMetrics(
                    lowerBoundSellerCount,
                    targetKsEvaluated,
                    deadlineHit,
                    bestCostUpdates,
                    partitionCount,
                    incumbentSource));
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
        private readonly SharedSearchIncumbent _sharedIncumbent;
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
            IReadOnlyList<int>? initialBestSelection,
            SharedSearchIncumbent sharedIncumbent)
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
            _sharedIncumbent = sharedIncumbent;

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
                        UpdateBestSelection(candidateCost);
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

            var bestCostHint = _sharedIncumbent.BestCostHint;
            if (!IsInfinite(bestCostHint) && lowerBound > bestCostHint + CostEpsilon)
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

        private void UpdateBestSelection(decimal candidateCost)
        {
            var candidateSelection = _selectedIndices.Take(_targetSellerCount).ToArray();
            _bestCost = candidateCost;
            _bestSelection = candidateSelection;
            _sharedIncumbent.TryUpdate(new SelectionResult(candidateSelection, candidateCost));
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
        decimal TotalCost,
        SearchPrecomputationContext? SearchContext,
        ExactSearchMetrics Metrics);

    private sealed record ExactSearchMetrics(
        int LowerBoundSellerCount,
        int TargetKsEvaluated,
        bool DeadlineHit,
        int BestCostUpdates,
        int PartitionCount,
        string IncumbentSource);

    private sealed record LegacyQuickSolveResult(
        IReadOnlyCollection<string> SelectedSellerNames,
        bool IsFullCoverage,
        LegacyQuickSolveMetrics Metrics);

    private sealed record LegacyQuickSolveMetrics(
        int LowerBoundSellerCount,
        int TargetKsEvaluated,
        int BestCostUpdates,
        int PartitionCount,
        string IncumbentSource);

    private sealed record FixedKSearchResult(
        SelectionResult? BestSelection,
        int PartitionCount,
        int IncumbentUpdates);

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

    private readonly record struct BruteForceSelectionResult(
        IReadOnlyList<int> SelectedOrderedSellerIndices,
        IReadOnlyList<int> UncoveredCardIndices,
        decimal TotalCost,
        int SelectionCountEvaluated);

    private sealed record AssignmentValidationResult(
        IReadOnlyCollection<string> SelectedSellerNames,
        IReadOnlyList<PurchaseAssignment> Assignments,
        decimal CardsTotalPrice,
        IReadOnlySet<string> UncoveredCardKeys,
        IReadOnlyCollection<string> AddedSellerNames);

    private sealed record FallbackPackCandidate(
        string SellerName,
        int CoveredCards,
        int CoveredUnits,
        decimal OneUnitPackCost,
        int OriginalOrder);

    private readonly record struct FallbackRescueResult(
        IReadOnlyList<int>? RescuedSelection,
        int TargetKsEvaluated,
        int BestCostUpdates,
        int PartitionCount);

    private readonly record struct LegacyFallbackOverrideResult(
        bool Applied,
        IReadOnlyList<string> SelectedSellerNames,
        decimal CurrentCost,
        decimal LegacyCost,
        decimal CurrentShippingAwareCost,
        decimal LegacyShippingAwareCost,
        decimal ShippingPenaltyFloor,
        decimal CurrentShippingPenalty,
        decimal LegacyShippingPenalty,
        int CurrentExtraSellerCount,
        int LegacyExtraSellerCount,
        string Reason);

    private sealed class SearchPrecomputationContext
    {
        public SearchPrecomputationContext(
            int[,] sellerCardQty,
            int[,] suffixCoverage,
            decimal[] fixedCostLowerBoundsBySellerCount,
            int sellerCount,
            int cardCount)
        {
            SellerCardQty = sellerCardQty;
            SuffixCoverage = suffixCoverage;
            FixedCostLowerBoundsBySellerCount = fixedCostLowerBoundsBySellerCount;
            SellerCount = sellerCount;
            CardCount = cardCount;
            MinimumCardCostLowerBound = InfiniteCost;
        }

        public int[,] SellerCardQty { get; }

        public int[,] SuffixCoverage { get; }

        public decimal[,,]? SuffixMinCosts { get; set; }

        public decimal[] FixedCostLowerBoundsBySellerCount { get; }

        public int SellerCount { get; }

        public int CardCount { get; }

        public decimal MinimumCardCostLowerBound { get; set; }
    }

    private sealed record ParallelSearchPartition(
        int StartFirstSellerIndex,
        int EndFirstSellerIndexExclusive,
        double Work);

    private sealed class SharedSearchIncumbent
    {
        private readonly object _sync = new();
        private SelectionResult? _bestSelection;
        private double _bestCostHint = double.PositiveInfinity;
        private int _updateCount;

        public SharedSearchIncumbent(SelectionResult? initialBestSelection)
        {
            if (initialBestSelection is null)
            {
                return;
            }

            _bestSelection = new SelectionResult(
                initialBestSelection.SelectedOrderedSellerIndices.ToArray(),
                initialBestSelection.TotalCost);
            _bestCostHint = (double)initialBestSelection.TotalCost;
        }

        public decimal BestCostHint
        {
            get
            {
                var bestCostHint = Volatile.Read(ref _bestCostHint);
                return double.IsPositiveInfinity(bestCostHint)
                    ? InfiniteCost
                    : (decimal)bestCostHint;
            }
        }

        public int UpdateCount => Volatile.Read(ref _updateCount);

        public SelectionResult? Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return _bestSelection is null
                        ? null
                        : new SelectionResult(
                            _bestSelection.SelectedOrderedSellerIndices.ToArray(),
                            _bestSelection.TotalCost);
                }
            }
        }

        public bool TryUpdate(SelectionResult candidate)
        {
            lock (_sync)
            {
                if (!IsBetterCandidate(candidate, _bestSelection))
                {
                    return false;
                }

                _bestSelection = new SelectionResult(
                    candidate.SelectedOrderedSellerIndices.ToArray(),
                    candidate.TotalCost);
                Volatile.Write(ref _bestCostHint, (double)candidate.TotalCost);
                Interlocked.Increment(ref _updateCount);
                return true;
            }
        }
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

}
