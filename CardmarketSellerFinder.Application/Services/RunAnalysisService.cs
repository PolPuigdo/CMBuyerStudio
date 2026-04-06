using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Optimization;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Domain.WantedCards;
using System;
using System.Collections.Generic;
using System.Text;

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

        public RunAnalysisService(
            IWantedCardsRepository wantedCardsRepository,
            IMarketDataCacheService marketDataCacheService,
            ICardMarketScraper cardMarketScraper,
            IHtmlReportGenerator htmlReportGenerator,
            OfferPurger offerPurger,
            PurchaseOptimizer purchaseOptimizer)
        {
            _wantedCardsRepository = wantedCardsRepository;
            _marketDataCacheService = marketDataCacheService;
            _cardMarketScraper = cardMarketScraper;
            _htmlReportGenerator = htmlReportGenerator;
            _offerPurger = offerPurger;
            _purchaseOptimizer = purchaseOptimizer;
        }


        public async Task RunAsync(bool eu = true, bool local = true, CancellationToken cancellationToken = default)
        {
            // Get cards from cards.json
            var wantedCards = await _wantedCardsRepository.GetAllAsync(cancellationToken);
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

            foreach (var target in targetsToScrape)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var marketData = await _cardMarketScraper.ScrapeAsync(target, cancellationToken);
                scrapedMarketData.Add(marketData);

                await _marketDataCacheService.SaveAsync(marketData, cancellationToken);
            }

            //Merge cached and scraped data
            var allMarketData = cachedMarketData.Concat(scrapedMarketData).ToList();

            // Purge useless sellers
            //var purgedMarketData = _offerPurger.Purge(allMarketData);

            //Run best seller eu if eu=true
            if (eu)
            {
                // TODO
            }

            //Run best seller local if local=true
            if (local)
            {
                // TODO
            }

            //Generate HTML report
            //await _htmlReportGenerator.GenerateAsync(xxxxxx, cancellationToken);
        }

        public async Task RunEUAsync()
        {
            await RunAsync(true, false);
        }

        public async Task RunLocalAsync()
        {
            await RunAsync(false, true);
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
    }
}
