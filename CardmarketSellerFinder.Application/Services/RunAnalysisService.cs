using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Enums;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Application.Optimization;
using CMBuyerStudio.Application.RunAnalysis;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Domain.WantedCards;
using CMBuyerStudio.Application.Common.Countries;
using Microsoft.Extensions.Options;
using CMBuyerStudio.Application.Common.Options;
using System.Diagnostics;
using System.Text.Json;

namespace CMBuyerStudio.Application.Services
{
    public class RunAnalysisService: IRunAnalysisService
    {
        private static readonly JsonSerializerOptions ProfileJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        private readonly IWantedCardsRepository _wantedCardsRepository;
        private readonly IMarketDataCacheService _marketDataCacheService;
        private readonly ICardMarketScraper _cardMarketScraper;
        private readonly IHtmlReportGenerator _htmlReportGenerator;
        private readonly OfferPurger _offerPurger;
        private readonly PurchaseOptimizer _purchaseOptimizer;
        private readonly IAppPaths _appPaths;

        private readonly decimal _defaultShippingCost;
        private readonly Dictionary<string, decimal> _shippingCostByCountryCode;

        public RunAnalysisService(
            IWantedCardsRepository wantedCardsRepository,
            IMarketDataCacheService marketDataCacheService,
            ICardMarketScraper cardMarketScraper,
            IHtmlReportGenerator htmlReportGenerator,
            OfferPurger offerPurger,
            PurchaseOptimizer purchaseOptimizer,
            IAppPaths appPaths,
            IOptions<ShippingCostsOptions> shippingOptions)
        {
            _wantedCardsRepository = wantedCardsRepository;
            _marketDataCacheService = marketDataCacheService;
            _cardMarketScraper = cardMarketScraper;
            _htmlReportGenerator = htmlReportGenerator;
            _offerPurger = offerPurger;
            _purchaseOptimizer = purchaseOptimizer;
            _appPaths = appPaths;
            _defaultShippingCost = (decimal)Math.Max(0, shippingOptions.Value.Default);
            _shippingCostByCountryCode = BuildShippingByCountryCode(shippingOptions.Value.Countries);
        }


        public async Task RunAsync(IProgress<RunProgressEvent> progress, CancellationToken cancellationToken = default)
        {
            // Get cards from cards.json
            var wantedCards = await _wantedCardsRepository.GetAllAsync(cancellationToken);
            progress.Report(new RunStartedEvent(wantedCards.Count));

            var scrapingTargets = BuildScrapingTargets(wantedCards);

            // Check cache for each card
            var cachedMarketData = new List<MarketCardData>();
            var targetsToScrape = new List<ScrapingTarget>();

            foreach (var target in scrapingTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cachedData = await _marketDataCacheService.GetAsync(target, cancellationToken);

                if (cachedData is not null)
                {
                    cachedMarketData.Add(cachedData);
                }
                else
                {
                    targetsToScrape.Add(target);
                }
            }

            // Scrap not cached cards
            var scrapedMarketData = new List<MarketCardData>();

            //await foreach (var marketData in _cardMarketScraper.ScrapeManyAsync(targetsToScrape, cancellationToken))
            //{
            //    scrapedMarketData.Add(marketData);
            //    await _marketDataCacheService.SaveAsync(marketData, cancellationToken);
            //}

            //Merge cached and scraped data
            var allMarketData = cachedMarketData.Concat(scrapedMarketData).ToList();
            var analysisBuildStopwatch = Stopwatch.StartNew();
            var analysisMarketData = BuildAnalysisMarketData(wantedCards, allMarketData);
            analysisBuildStopwatch.Stop();

            var analysisBuildPhase = new OptimizationPhaseProfile
            {
                Name = "Setup.BuildAnalysisMarketData",
                ElapsedMilliseconds = analysisBuildStopwatch.ElapsedMilliseconds,
                Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["wantedGroups"] = wantedCards.Count,
                    ["rawMarketCards"] = allMarketData.Count,
                    ["logicalCards"] = analysisMarketData.Count,
                    ["offers"] = analysisMarketData.Sum(card => card.Offers.Count)
                }
            };

            var compactionStopwatch = Stopwatch.StartNew();
            var compactedAnalysis = CompactAnalysisMarketData(analysisMarketData);
            compactionStopwatch.Stop();
            var compactionPhase = new OptimizationPhaseProfile
            {
                Name = "Setup.CompactAggregatedOffers",
                ElapsedMilliseconds = compactionStopwatch.ElapsedMilliseconds,
                Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["logicalCards"] = compactedAnalysis.MarketData.Count,
                    ["offersBefore"] = compactedAnalysis.OffersBefore,
                    ["offersAfterExactMerge"] = compactedAnalysis.OffersAfterExactMerge,
                    ["offersAfterFrontierTrim"] = compactedAnalysis.OffersAfterFrontierTrim
                }
            };
            progress.Report(new CalculationProfileSnapshotEvent(
                "Setup",
                $"cards {compactedAnalysis.MarketData.Count} | offers {compactedAnalysis.OffersBefore}->{compactedAnalysis.OffersAfterFrontierTrim} after compaction"));

            // Purge useless sellers
            var euPreparation = BuildPurgedScopeSnapshot(compactedAnalysis.MarketData, SellerScopeMode.Eu);
            var localPreparation = BuildPurgedScopeSnapshot(compactedAnalysis.MarketData, SellerScopeMode.Local);
            var sharedSetupPhases = new List<OptimizationPhaseProfile> { analysisBuildPhase, compactionPhase };

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new CalculationStartedEvent("EU"));
            progress.Report(new CalculationProfileSnapshotEvent("EU", BuildPreparationSummary("EU", euPreparation)));
            var euOptimizationRawResult = _purchaseOptimizer.Optimize(euPreparation.Snapshot);
            var euOptimizationResult = AttachRunProfile(
                euOptimizationRawResult,
                BuildRunProfile("EU", [.. sharedSetupPhases, .. euPreparation.ProfilePhases], euOptimizationRawResult.ProfilePhases));
            progress.Report(new CalculationProfileCompletedEvent("EU", BuildCompletionSummary(euOptimizationResult.RunProfile!)));
            progress.Report(new CalculationFinishedEvent("EU"));

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new CalculationStartedEvent("Local"));
            progress.Report(new CalculationProfileSnapshotEvent("Local", BuildPreparationSummary("Local", localPreparation)));
            var localOptimizationRawResult = _purchaseOptimizer.Optimize(localPreparation.Snapshot);
            var localOptimizationResult = AttachRunProfile(
                localOptimizationRawResult,
                BuildRunProfile("Local", [.. sharedSetupPhases, .. localPreparation.ProfilePhases], localOptimizationRawResult.ProfilePhases));
            progress.Report(new CalculationProfileCompletedEvent("Local", BuildCompletionSummary(localOptimizationResult.RunProfile!)));
            progress.Report(new CalculationFinishedEvent("Local"));

            var reportGeneratedAt = DateTimeOffset.Now;
            await WriteProfilesAsync(reportGeneratedAt, [euOptimizationResult.RunProfile!, localOptimizationResult.RunProfile!], cancellationToken);
            var euReport = await _htmlReportGenerator.GenerateAsync(
                new HtmlReportRequest
                {
                    Scope = SellerScopeMode.Eu,
                    OptimizationResult = euOptimizationResult,
                    Snapshot = euPreparation.Snapshot,
                    GeneratedAt = reportGeneratedAt
                },
                cancellationToken);
            progress.Report(new ReportGeneratedEvent(euReport.Path, GetScopeLabel(euReport.Scope)));

            var localReport = await _htmlReportGenerator.GenerateAsync(
                new HtmlReportRequest
                {
                    Scope = SellerScopeMode.Local,
                    OptimizationResult = localOptimizationResult,
                    Snapshot = localPreparation.Snapshot,
                    GeneratedAt = reportGeneratedAt
                },
                cancellationToken);
            progress.Report(new ReportGeneratedEvent(localReport.Path, GetScopeLabel(localReport.Scope)));

            progress.Report(new RunCompletedEvent());
        }

        private static IReadOnlyList<ScrapingTarget> BuildScrapingTargets(IEnumerable<WantedCardGroup> wantedCards)
        {
            var targets = new List<ScrapingTarget>();

            foreach (var group in wantedCards)
            {
                var cardName = group.CardName?.Trim();
                if (string.IsNullOrWhiteSpace(cardName) || group.DesiredQuantity <= 0)
                {
                    continue;
                }

                var requestKey = BuildRequestKey(cardName);

                foreach (var variant in group.Variants)
                {
                    if (string.IsNullOrWhiteSpace(variant.ProductUrl))
                    {
                        continue;
                    }

                    targets.Add(new ScrapingTarget
                    {
                        RequestKey = requestKey,
                        CardName = cardName,
                        SetName = variant.SetName,
                        ProductUrl = variant.ProductUrl,
                        DesiredQuantity = group.DesiredQuantity
                    });
                }
            }

            return targets;
        }

        private static IReadOnlyList<MarketCardData> BuildAnalysisMarketData(
            IReadOnlyList<WantedCardGroup> wantedCards,
            IReadOnlyList<MarketCardData> allMarketData)
        {
            var marketDataByProductUrl = allMarketData
                .Where(x => !string.IsNullOrWhiteSpace(x.Target.ProductUrl))
                .GroupBy(x => x.Target.ProductUrl, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var result = new List<MarketCardData>();

            foreach (var group in wantedCards)
            {
                var cardName = group.CardName?.Trim();
                if (string.IsNullOrWhiteSpace(cardName) || group.DesiredQuantity <= 0)
                {
                    continue;
                }

                var variants = group.Variants
                    .Where(variant => !string.IsNullOrWhiteSpace(variant.ProductUrl))
                    .GroupBy(variant => variant.ProductUrl, StringComparer.OrdinalIgnoreCase)
                    .Select(groupedVariant => groupedVariant.First())
                    .ToList();

                if (variants.Count == 0)
                {
                    continue;
                }

                var offers = new List<SellerOffer>();
                var scrapedAtUtc = DateTime.MinValue;

                foreach (var variant in variants)
                {
                    if (!marketDataByProductUrl.TryGetValue(variant.ProductUrl!, out var marketData))
                    {
                        continue;
                    }

                    if (marketData.ScrapedAtUtc > scrapedAtUtc)
                    {
                        scrapedAtUtc = marketData.ScrapedAtUtc;
                    }

                    offers.AddRange(marketData.Offers.Select(offer => new SellerOffer
                    {
                        SellerName = offer.SellerName,
                        Country = offer.Country,
                        Price = offer.Price,
                        AvailableQuantity = offer.AvailableQuantity,
                        CardName = cardName,
                        SetName = offer.SetName,
                        ProductUrl = offer.ProductUrl
                    }));
                }

                var representativeVariant = variants[0];
                result.Add(new MarketCardData
                {
                    Target = new ScrapingTarget
                    {
                        RequestKey = BuildRequestKey(cardName),
                        CardName = cardName,
                        SetName = variants.Count == 1 ? representativeVariant.SetName : string.Empty,
                        ProductUrl = representativeVariant.ProductUrl!,
                        DesiredQuantity = group.DesiredQuantity
                    },
                    ScrapedAtUtc = scrapedAtUtc,
                    Offers = offers
                });
            }

            return result;
        }

        private static CompactedAnalysisResult CompactAnalysisMarketData(
            IReadOnlyList<MarketCardData> marketData)
        {
            var offersBefore = marketData.Sum(card => card.Offers.Count);
            var offersAfterExactMerge = 0;
            var offersAfterFrontierTrim = 0;
            var compactedCards = new List<MarketCardData>(marketData.Count);

            foreach (var card in marketData)
            {
                var exactMergedOffers = card.Offers
                    .Where(offer => offer.AvailableQuantity > 0)
                    .GroupBy(ExactOfferGroupKey.FromOffer)
                    .Select(group => new SellerOffer
                    {
                        SellerName = group.First().SellerName,
                        Country = group.First().Country,
                        Price = group.First().Price,
                        AvailableQuantity = group.Sum(offer => offer.AvailableQuantity),
                        CardName = group.First().CardName,
                        SetName = group.First().SetName,
                        ProductUrl = group.First().ProductUrl
                    })
                    .ToList();

                offersAfterExactMerge += exactMergedOffers.Count;

                var frontierOffers = exactMergedOffers
                    .GroupBy(offer => offer.SellerName, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => CompactSellerFrontier(group.Key, group, card.Target.DesiredQuantity))
                    .OrderBy(offer => offer.SellerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.Price)
                    .ThenBy(offer => offer.ProductUrl, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.SetName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                offersAfterFrontierTrim += frontierOffers.Count;

                compactedCards.Add(new MarketCardData
                {
                    Target = card.Target,
                    ScrapedAtUtc = card.ScrapedAtUtc,
                    Offers = frontierOffers
                });
            }

            return new CompactedAnalysisResult(
                compactedCards,
                offersBefore,
                offersAfterExactMerge,
                offersAfterFrontierTrim);
        }

        private static IEnumerable<SellerOffer> CompactSellerFrontier(
            string sellerName,
            IEnumerable<SellerOffer> offers,
            int desiredQuantity)
        {
            var remainingQuantity = Math.Max(0, desiredQuantity);
            if (remainingQuantity <= 0)
            {
                yield break;
            }

            foreach (var offer in offers
                .OrderBy(offer => offer.Price)
                .ThenByDescending(offer => offer.AvailableQuantity)
                .ThenBy(offer => offer.ProductUrl, StringComparer.OrdinalIgnoreCase)
                .ThenBy(offer => offer.SetName, StringComparer.OrdinalIgnoreCase))
            {
                if (remainingQuantity <= 0)
                {
                    yield break;
                }

                var take = Math.Min(offer.AvailableQuantity, remainingQuantity);
                if (take <= 0)
                {
                    continue;
                }

                yield return new SellerOffer
                {
                    SellerName = sellerName,
                    Country = offer.Country,
                    Price = offer.Price,
                    AvailableQuantity = take,
                    CardName = offer.CardName,
                    SetName = offer.SetName,
                    ProductUrl = offer.ProductUrl
                };

                remainingQuantity -= take;
            }
        }

        private static string BuildRequestKey(string cardName)
            => cardName.Trim();

        private ScopePreparationResult BuildPurgedScopeSnapshot(
        IReadOnlyList<MarketCardData> allMarketData,
        SellerScopeMode scope)
        {
            var applyScopeStopwatch = Stopwatch.StartNew();
            var scopedMarketData = ApplyScope(allMarketData, scope);
            applyScopeStopwatch.Stop();

            var fixedCostStopwatch = Stopwatch.StartNew();
            var fixedCostBySellerName = BuildFixedCostBySeller(scopedMarketData);
            fixedCostStopwatch.Stop();

            var purgeResult = _offerPurger.Purge(scopedMarketData, fixedCostBySellerName);

            return new ScopePreparationResult(
                new PurgedScopeSnapshot
                {
                    ScopedMarketData = purgeResult.ScopedMarketData,
                    PurgedMarketData = purgeResult.PurgedMarketData,
                    RemainingRequiredByCardKey = purgeResult.RemainingRequiredByCardKey,
                    FixedCostBySellerName = fixedCostBySellerName,
                    PreselectedSellerNames = purgeResult.PreselectedSellerNames,
                    UncoveredCardKeys = purgeResult.UncoveredCardKeys
                },
                [
                    new OptimizationPhaseProfile
                    {
                        Name = $"{GetScopeLabel(scope)}.ApplyScope",
                        ElapsedMilliseconds = applyScopeStopwatch.ElapsedMilliseconds,
                        Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["cards"] = scopedMarketData.Count,
                            ["offers"] = scopedMarketData.Sum(card => card.Offers.Count)
                        }
                    },
                    new OptimizationPhaseProfile
                    {
                        Name = $"{GetScopeLabel(scope)}.BuildFixedCostBySeller",
                        ElapsedMilliseconds = fixedCostStopwatch.ElapsedMilliseconds,
                        Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["sellers"] = fixedCostBySellerName.Count
                        }
                    },
                    .. purgeResult.ProfilePhases
                ]);
        }

        private IReadOnlyList<MarketCardData> ApplyScope(
        IReadOnlyList<MarketCardData> marketData,
        SellerScopeMode scope)
        {
            return marketData
                .Select(card => new MarketCardData
                {
                    Target = card.Target,
                    ScrapedAtUtc = card.ScrapedAtUtc,
                    Offers = card.Offers
                        .Where(offer => ShouldKeepOffer(offer, scope))
                        .ToList()
                })
                .ToList();
        }

        private static bool ShouldKeepOffer(SellerOffer offer, SellerScopeMode scope)
        {
            if (!CountryCatalog.TryGetCountryCode(offer.Country, out var countryCode))
            {
                return false;
            }

            return scope switch
            {
                SellerScopeMode.Eu => CountryCatalog.DefaultEuCountryCodes.Contains(countryCode),
                SellerScopeMode.Local => string.Equals(countryCode, "ES", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private Dictionary<string, decimal> BuildFixedCostBySeller(
        IReadOnlyList<MarketCardData> marketData)
        {
            var sellerCountryByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var offer in marketData.SelectMany(card => card.Offers))
            {
                if (!sellerCountryByName.ContainsKey(offer.SellerName))
                {
                    sellerCountryByName[offer.SellerName] = offer.Country;
                }
            }

            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in sellerCountryByName)
            {
                result[pair.Key] = ResolveShippingCost(pair.Value);
            }

            return result;
        }

        private decimal ResolveShippingCost(string country)
        {
            if (CountryCatalog.TryGetCountryCode(country, out var countryCode)
                && _shippingCostByCountryCode.TryGetValue(countryCode, out var cost))
            {
                return cost;
            }

            return _defaultShippingCost;
        }

        private static string BuildPreparationSummary(string scope, ScopePreparationResult preparation)
        {
            var scopedCards = preparation.Snapshot.ScopedMarketData.Count;
            var scopedOffers = preparation.Snapshot.ScopedMarketData.Sum(card => card.Offers.Count);
            var remainingSellers = preparation.Snapshot.PurgedMarketData
                .SelectMany(card => card.Offers)
                .Select(offer => offer.SellerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return $"{scope}: cards {scopedCards} | offers {scopedOffers} | sellers after purge {remainingSellers}";
        }

        private static PurchaseOptimizationResult AttachRunProfile(
            PurchaseOptimizationResult result,
            OptimizationRunProfile runProfile)
        {
            return new PurchaseOptimizationResult
            {
                SelectedSellerNames = result.SelectedSellerNames,
                Assignments = result.Assignments,
                SellerCount = result.SellerCount,
                CardsTotalPrice = result.CardsTotalPrice,
                UncoveredCardKeys = result.UncoveredCardKeys,
                ProfilePhases = result.ProfilePhases,
                RunProfile = runProfile
            };
        }

        private static OptimizationRunProfile BuildRunProfile(
            string scope,
            IReadOnlyList<OptimizationPhaseProfile> serviceAndPurgePhases,
            IReadOnlyList<OptimizationPhaseProfile> optimizerPhases)
        {
            var phases = serviceAndPurgePhases
                .Concat(optimizerPhases)
                .ToList();

            var totalElapsedMilliseconds = phases.Sum(phase => phase.ElapsedMilliseconds);
            var candidatePoolPhase = phases.FirstOrDefault(phase => phase.Name == "Optimize.CandidatePool");
            var exactPhase = phases.FirstOrDefault(phase => phase.Name == "Optimize.ReducedExact");

            return new OptimizationRunProfile
            {
                Scope = scope,
                TotalElapsedMilliseconds = totalElapsedMilliseconds,
                Phases = phases,
                Counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["phaseCount"] = phases.Count,
                    ["candidateSellers"] = candidatePoolPhase?.Counters.GetValueOrDefault("candidateSellers") ?? 0,
                    ["exactParallelism"] = exactPhase?.Counters.GetValueOrDefault("parallelism") ?? 0
                },
                Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["generatedAtUtc"] = DateTime.UtcNow.ToString("O")
                }
            };
        }

        private static string BuildCompletionSummary(OptimizationRunProfile profile)
        {
            var candidatePoolPhase = profile.Phases.FirstOrDefault(phase => phase.Name == "Optimize.CandidatePool");
            var exactPhase = profile.Phases.FirstOrDefault(phase => phase.Name == "Optimize.ReducedExact");
            var beamPhase = profile.Phases.FirstOrDefault(phase => phase.Name == "Optimize.BeamSearch");
            var assignmentPhase = profile.Phases.FirstOrDefault(phase => phase.Name == "Optimize.BuildAssignments");

            return $"{profile.Scope}: total {profile.TotalElapsedMilliseconds} ms | candidates {candidatePoolPhase?.Counters.GetValueOrDefault("candidateSellers") ?? 0} | beam {beamPhase?.ElapsedMilliseconds ?? 0} ms | exact {exactPhase?.ElapsedMilliseconds ?? 0} ms | assignments {assignmentPhase?.ElapsedMilliseconds ?? 0} ms";
        }

        private async Task WriteProfilesAsync(
            DateTimeOffset generatedAt,
            IReadOnlyList<OptimizationRunProfile> profiles,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_appPaths.LogsPath);
            var path = Path.Combine(_appPaths.LogsPath, $"best-seller-profile-{generatedAt:yyyyMMdd-HHmmss}.json");

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(
                stream,
                new
                {
                    GeneratedAt = generatedAt,
                    Profiles = profiles
                },
                ProfileJsonOptions,
                cancellationToken);
        }

        private static Dictionary<string, decimal> BuildShippingByCountryCode(
        IReadOnlyDictionary<string, double> configuredCountries)
        {
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in configuredCountries)
            {
                if (entry.Value < 0)
                    continue;

                if (CountryCatalog.TryGetCountryCode(entry.Key, out var countryCode))
                {
                    result[countryCode] = (decimal)entry.Value;
                }
            }

            return result;
        }

        private static string GetScopeLabel(SellerScopeMode scope)
            => scope == SellerScopeMode.Eu ? "EU" : "Local";

        private sealed record ScopePreparationResult(
            PurgedScopeSnapshot Snapshot,
            IReadOnlyList<OptimizationPhaseProfile> ProfilePhases);

        private sealed record CompactedAnalysisResult(
            IReadOnlyList<MarketCardData> MarketData,
            int OffersBefore,
            int OffersAfterExactMerge,
            int OffersAfterFrontierTrim);

        private sealed record ExactOfferGroupKey(
            string SellerName,
            string ProductUrl,
            string SetName,
            decimal Price)
        {
            public static ExactOfferGroupKey FromOffer(SellerOffer offer)
                => new(
                    offer.SellerName.Trim().ToUpperInvariant(),
                    offer.ProductUrl.Trim().ToUpperInvariant(),
                    (offer.SetName ?? string.Empty).Trim().ToUpperInvariant(),
                    offer.Price);
        }
    }
}
