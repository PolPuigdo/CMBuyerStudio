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

namespace CMBuyerStudio.Application.Services
{
    public class RunAnalysisService: IRunAnalysisService
    {
        private readonly IWantedCardsRepository _wantedCardsRepository;
        private readonly IMarketDataCacheService _marketDataCacheService;
        private readonly ICardMarketScraper _cardMarketScraper;
        private readonly IHtmlReportGenerator _htmlReportGenerator;
        private readonly OfferPurger _offerPurger;
        private readonly PurchaseOptimizer _purchaseOptimizer;

        private readonly decimal _defaultShippingCost;
        private readonly Dictionary<string, decimal> _shippingCostByCountryCode;

        public RunAnalysisService(
            IWantedCardsRepository wantedCardsRepository,
            IMarketDataCacheService marketDataCacheService,
            ICardMarketScraper cardMarketScraper,
            IHtmlReportGenerator htmlReportGenerator,
            OfferPurger offerPurger,
            PurchaseOptimizer purchaseOptimizer,
            IOptions<ShippingCostsOptions> shippingOptions)
        {
            _wantedCardsRepository = wantedCardsRepository;
            _marketDataCacheService = marketDataCacheService;
            _cardMarketScraper = cardMarketScraper;
            _htmlReportGenerator = htmlReportGenerator;
            _offerPurger = offerPurger;
            _purchaseOptimizer = purchaseOptimizer;
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

            await foreach (var marketData in _cardMarketScraper.ScrapeManyAsync(targetsToScrape, cancellationToken))
            {
                scrapedMarketData.Add(marketData);
                await _marketDataCacheService.SaveAsync(marketData, cancellationToken);
            }

            //Merge cached and scraped data
            var allMarketData = cachedMarketData.Concat(scrapedMarketData).ToList();

            // Purge useless sellers
            var euPurgedSnapshot = BuildPurgedScopeSnapshot(allMarketData, SellerScopeMode.Eu);
            var localPurgedSnapshot = BuildPurgedScopeSnapshot(allMarketData, SellerScopeMode.Local);

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new CalculationStartedEvent("EU"));
            var euOptimizationResult = _purchaseOptimizer.Optimize(euPurgedSnapshot);
            progress.Report(new CalculationFinishedEvent("EU"));

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new CalculationStartedEvent("Local"));
            var localOptimizationResult = _purchaseOptimizer.Optimize(localPurgedSnapshot);
            progress.Report(new CalculationFinishedEvent("Local"));

            var reportGeneratedAt = DateTimeOffset.Now;
            var euReport = await _htmlReportGenerator.GenerateAsync(
                new HtmlReportRequest
                {
                    Scope = SellerScopeMode.Eu,
                    OptimizationResult = euOptimizationResult,
                    Snapshot = euPurgedSnapshot,
                    GeneratedAt = reportGeneratedAt
                },
                cancellationToken);
            progress.Report(new ReportGeneratedEvent(euReport.Path, GetScopeLabel(euReport.Scope)));

            var localReport = await _htmlReportGenerator.GenerateAsync(
                new HtmlReportRequest
                {
                    Scope = SellerScopeMode.Local,
                    OptimizationResult = localOptimizationResult,
                    Snapshot = localPurgedSnapshot,
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
                if (string.IsNullOrWhiteSpace(group.CardName) || group.DesiredQuantity <= 0)
                {
                    continue;
                }

                foreach (var variant in group.Variants)
                {
                    if (string.IsNullOrWhiteSpace(variant.ProductUrl))
                    {
                        continue;
                    }

                    targets.Add(new ScrapingTarget
                    {
                        CardName = group.CardName,
                        SetName = variant.SetName,
                        ProductUrl = variant.ProductUrl,
                        DesiredQuantity = group.DesiredQuantity
                    });
                }
            }

            return targets;
        }

        private PurgedScopeSnapshot BuildPurgedScopeSnapshot(
        IReadOnlyList<MarketCardData> allMarketData,
        SellerScopeMode scope)
        {
            var scopedMarketData = ApplyScope(allMarketData, scope);
            var fixedCostBySellerName = BuildFixedCostBySeller(scopedMarketData);
            var purgeResult = _offerPurger.Purge(scopedMarketData, fixedCostBySellerName);

            return new PurgedScopeSnapshot
            {
                ScopedMarketData = purgeResult.ScopedMarketData,
                PurgedMarketData = purgeResult.PurgedMarketData,
                RemainingRequiredByCardKey = purgeResult.RemainingRequiredByCardKey,
                FixedCostBySellerName = fixedCostBySellerName,
                PreselectedSellerNames = purgeResult.PreselectedSellerNames,
                UncoveredCardKeys = purgeResult.UncoveredCardKeys
            };
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
    }
}
