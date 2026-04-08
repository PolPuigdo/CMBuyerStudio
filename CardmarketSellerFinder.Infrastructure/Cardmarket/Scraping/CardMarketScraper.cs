using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Infrastructure.Cardmarket.Builders;
using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using CMBuyerStudio.Infrastructure.Cardmarket.Parsing;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Locators;
using CMBuyerStudio.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping
{
    public class CardMarketScraper : ICardMarketScraper
    {
        private readonly PlaywrightBuilder _playwrightBuilder;
        private readonly ScrapingOptions _scrapingOptions;
        private readonly CardmarketSessionSetup _setup;

        private const decimal PriceMaxNum = 0.50m;
        private const decimal PriceMaxPercent = 10m;
        private const int LoadMoreMaxClicks = 5;
        private const int LoadMoreRowsWait = 5000;

        public CardMarketScraper(PlaywrightBuilder playwrightBuilder, CardmarketSessionSetup setup, IOptions<ScrapingOptions> scrapingOptions)
        {
            _playwrightBuilder = playwrightBuilder;
            _scrapingOptions = scrapingOptions.Value;
            _setup = setup;
        }

        public Task<MarketCardData> ScrapeAsync(ScrapingTarget target, CancellationToken cancellationToken = default)
        {
            return ScrapeAsync(
                target,
                proxy: null,
                useChromium: true,
                cancellationToken);
        }

        private async Task<MarketCardData> ScrapeAsync(ScrapingTarget target, Proxy? proxy, bool useChromium, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);

            await using var session = await CreateSessionAsync(useChromium, proxy, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var filteredUrl = UrlBuilder.BuildFilteredCardUrl(target.ProductUrl, _scrapingOptions);

            await _setup.PrepareAsync(session.Page, filteredUrl, cancellationToken);

            var firstPrice = await GetFirstPriceAsync(session.Page);
            var maxPrice = CalculateMaxPrice(firstPrice);

            await ExpandResultsAsync(session.Page, maxPrice);

            var offers = await CollectOffersWithinMaxPriceAsync(
                session.Page,
                target,
                maxPrice);

            await Task.Delay(WaitTiming.GetRandom(15000, 20000), cancellationToken);

            return new MarketCardData
            {
                Target = target,
                Offers = offers,
                ScrapedAtUtc = DateTime.UtcNow
            };
        }

        public async IAsyncEnumerable<MarketCardData> ScrapeManyAsync(IEnumerable<ScrapingTarget> targets, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(targets);

            var targetQueue = new ConcurrentQueue<ScrapingTarget>(targets);
            var resultChannel = Channel.CreateUnbounded<MarketCardData>();

            var workers = CreateWorkers(targetQueue, resultChannel.Writer, cancellationToken);

            _ = Task.WhenAll(workers).ContinueWith(async task =>
            {
                if (task.Exception is not null)
                {
                    resultChannel.Writer.TryComplete(task.Exception.GetBaseException());
                    return;
                }

                resultChannel.Writer.TryComplete();
                await Task.CompletedTask;
            }, cancellationToken);

            await foreach (var marketData in resultChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return marketData;
            }
        }

        private List<Task> CreateWorkers(ConcurrentQueue<ScrapingTarget> targetQueue, ChannelWriter<MarketCardData> writer, CancellationToken cancellationToken)
        {
            var workers = new List<Task>
            {
                RunWorkerAsync(
                    targetQueue,
                    writer,
                    proxy: null,
                    useChromium: true,
                    cancellationToken)
            };

            if (_scrapingOptions.Proxies is { Count: > 0 })
            {
                for (var i = 0; i < _scrapingOptions.Proxies.Count; i++)
                {
                    var proxy = ToPlaywrightProxy(_scrapingOptions.Proxies[i]);
                    var useChromium = i % 2 == 0;

                    workers.Add(RunWorkerAsync(
                        targetQueue,
                        writer,
                        proxy,
                        useChromium,
                        cancellationToken));
                }
            }

            return workers;
        }

        private async Task RunWorkerAsync(
            ConcurrentQueue<ScrapingTarget> targetQueue,
            ChannelWriter<MarketCardData> writer,
            Proxy? proxy,
            bool useChromium,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && targetQueue.TryDequeue(out var target))
            {
                var marketData = await ScrapeAsync(
                    target,
                    proxy,
                    useChromium,
                    cancellationToken);

                await writer.WriteAsync(marketData, cancellationToken);
            }
        }

        private async Task<PlaywrightSession> CreateSessionAsync(bool useChromium, Proxy? proxy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (useChromium)
            {
                return await _playwrightBuilder.CreateChromiumAsync(
                    headless: _scrapingOptions.Headless,
                    proxy: proxy);
            }

            return await _playwrightBuilder.CreateFirefoxAsync(
                headless: _scrapingOptions.Headless,
                proxy: proxy);
        }

        private static Proxy ToPlaywrightProxy(ProxyOptions proxyOptions)
        {
            return new Proxy
            {
                Server = proxyOptions.Server,
                Username = string.IsNullOrWhiteSpace(proxyOptions.Username) ? null : proxyOptions.Username,
                Password = string.IsNullOrWhiteSpace(proxyOptions.Password) ? null : proxyOptions.Password
            };
        }

        private static decimal CalculateMaxPrice(decimal firstPrice)
        {
            var maxPrice1 = firstPrice + PriceMaxNum;
            var maxPrice2 = firstPrice + (PriceMaxPercent * firstPrice / 100m);
            return Math.Max(maxPrice1, maxPrice2);
        }

        private static async Task<decimal> GetFirstPriceAsync(IPage page)
        {
            var firstRow = page
                .Locator(CardmarketLocators.Offers.OfferRow)
                .First;

            return await GetPriceFromRow(firstRow);
        }

        private static async Task<decimal> GetLastPriceAsync(IPage page)
        {
            var lastRow = page
                .Locator(CardmarketLocators.Offers.OfferRow)
                .Last;

            return await GetPriceFromRow(lastRow);
        }

        private static async Task<decimal> GetPriceFromRow(ILocator row)
        {
            var priceLocator = row.Locator(CardmarketLocators.Offers.Price).Last;

            await priceLocator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5000
            });

            var rawText = await priceLocator.InnerTextAsync();

            return CardmarketValueParsers.TryParseEuroPrice(rawText, out decimal firstPrice)
                ? firstPrice
                : 0m;
        }

        private static async Task ExpandResultsAsync(IPage page, decimal maxPrice)
        {
            var clickTries = 0;

            for (var click = 0; click < LoadMoreMaxClicks; click++)
            {
                var loadMoreButton = page.Locator(CardmarketLocators.Offers.LoadMoreButton);
                if (await loadMoreButton.CountAsync() == 0)
                {
                    break;
                }

                var button = loadMoreButton.First;

                try
                {
                    if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync())
                    {
                        if (clickTries == 0)
                        {
                            clickTries = 1;
                            await Task.Delay(WaitTiming.GetOneSecDiff(LoadMoreRowsWait));
                            continue;
                        }
                        else 
                        {
                            break;
                        }
                    }

                    await button.ClickAsync();
                    clickTries = 0;
                    await Task.Delay(WaitTiming.GetOneSecDiff(LoadMoreRowsWait));

                    decimal lastPrice = await GetLastPriceAsync(page);
                    if (lastPrice > maxPrice)
                    {
                        break;
                    }
                }
                catch (PlaywrightException)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        }

        private async Task<IReadOnlyList<SellerOffer>> CollectOffersWithinMaxPriceAsync(IPage page, ScrapingTarget target, decimal maxPrice)
        {
            var offers = new List<SellerOffer>();

            var rows = page.Locator(CardmarketLocators.Offers.OfferRow);
            await rows.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible
            });

            var rowCount = await rows.CountAsync();

            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var offer = await TryExtractOfferAsync(rows.Nth(rowIndex), target);
                if (offer is null)
                {
                    continue;
                }

                if (offer.Price > maxPrice)
                {
                    break;
                }

                offers.Add(offer);
            }

            return offers;            
        }

        private static async Task<SellerOffer?> TryExtractOfferAsync(ILocator row, ScrapingTarget target)
        {
            if (!await CardmarketValueParsers.IsPurchasableAsync(row))
            {
                return null;
            }

            if (await CardmarketValueParsers.HasNonCertifiedShippingWarningAsync(row))
            {
                return null;
            }

            var (sellerName, _) = await CardmarketValueParsers.GetSellerInfoAsync(row);
            if (string.IsNullOrWhiteSpace(sellerName))
            {
                return null;
            }

            var price = await GetPriceFromRow(row);

            var quantity = await CardmarketValueParsers.TryExtractQuantityAsync(row);
            if (quantity <= 0)
            {
                quantity = 1;
            }

            var country = await CardmarketValueParsers.TryGetCountryFromTooltipAsync(row);

            return new SellerOffer
            {
                SellerName = sellerName,
                Country = country,
                Price = price,
                AvailableQuantity = quantity,
                CardName = target.CardName,
                SetName = target.SetName,
                ProductUrl = target.ProductUrl
            };
        }
    }
}
