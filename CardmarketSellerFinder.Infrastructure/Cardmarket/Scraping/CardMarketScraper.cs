using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using CMBuyerStudio.Infrastructure.Cardmarket.Parsing;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Locators;
using CMBuyerStudio.Infrastructure.Options;
using Microsoft.Playwright;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping;

public class CardMarketScraper : ICardMarketScraper
{
    private const decimal PriceMaxNum = 0.50m;
    private const decimal PriceMaxPercent = 50m;
    private const int LoadMoreMaxClicks = 5;
    private const int LoadMoreRowsWait = 5000;
    private const int MaxScrapeAttemptsPerTarget = 3;
    private const int MaxConsecutiveWorkerFailures = 3;

    private readonly IPlaywrightSessionFactory _playwrightSessionFactory;
    private readonly IAppSettingsService _appSettingsService;
    private readonly ICardmarketSessionSetup _setup;
    private readonly PlaywrightProxyService _playwrightProxyService;
    private readonly IScrapeDelayStrategy _scrapeDelayStrategy;

    public CardMarketScraper(
        IPlaywrightSessionFactory playwrightSessionFactory,
        ICardmarketSessionSetup setup,
        PlaywrightProxyService playwrightProxyService,
        IAppSettingsService appSettingsService,
        IScrapeDelayStrategy scrapeDelayStrategy)
    {
        _playwrightSessionFactory = playwrightSessionFactory;
        _appSettingsService = appSettingsService;
        _playwrightProxyService = playwrightProxyService;
        _setup = setup;
        _scrapeDelayStrategy = scrapeDelayStrategy;
    }

    public async Task<MarketCardData> ScrapeAsync(ScrapingTarget target, CancellationToken cancellationToken = default)
    {
        var scrapingOptions = await BuildScrapingOptionsAsync(cancellationToken);

        return await ScrapeAsync(
            target,
            proxy: null,
            useChromium: true,
            scrapingOptions,
            cancellationToken);
    }

    private async Task<MarketCardData> ScrapeAsync(
        ScrapingTarget target,
        Proxy? proxy,
        bool useChromium,
        ScrapingOptions scrapingOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        await using var session = await CreateSessionAsync(
            useChromium,
            proxy,
            scrapingOptions.Headless,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var filteredUrl = UrlBuilder.BuildFilteredCardUrl(target.ProductUrl, scrapingOptions);

        await _setup.PrepareAsync(session.Page, filteredUrl, cancellationToken);

        var firstPrice = await GetFirstPriceAsync(session.Page);
        var maxPrice = CalculateMaxPrice(firstPrice);

        await ExpandResultsAsync(session.Page, maxPrice);

        var offers = await CollectOffersWithinMaxPriceAsync(
            session.Page,
            target,
            maxPrice);

        await _scrapeDelayStrategy.DelayAfterScrapeAsync(cancellationToken);

        return new MarketCardData
        {
            Target = target,
            Offers = offers,
            ScrapedAtUtc = DateTime.UtcNow
        };
    }

    public async IAsyncEnumerable<MarketCardData> ScrapeManyAsync(
        IEnumerable<ScrapingTarget> targets,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var workChannel = Channel.CreateUnbounded<ScrapeWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        var resultChannel = Channel.CreateUnbounded<MarketCardData>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var targetList = targets.ToList();
        var pendingWorkItems = targetList.Count;

        if (pendingWorkItems == 0)
        {
            resultChannel.Writer.TryComplete();

            await foreach (var marketData in resultChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return marketData;
            }

            yield break;
        }

        var scrapingOptions = await BuildScrapingOptionsAsync(cancellationToken);
        var workingProxies = await _playwrightProxyService.GetWorkingProxiesAsync(cancellationToken);

        foreach (var target in targetList)
        {
            await workChannel.Writer.WriteAsync(new ScrapeWorkItem
            {
                Target = target,
                Attempt = 1
            }, cancellationToken);
        }

        var workers = CreateWorkers(
            workChannel.Reader,
            workChannel.Writer,
            resultChannel.Writer,
            workingProxies,
            scrapingOptions,
            () => Interlocked.Decrement(ref pendingWorkItems) == 0,
            cancellationToken);

        _ = Task.WhenAll(workers).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                resultChannel.Writer.TryComplete(task.Exception?.GetBaseException());
                return;
            }

            resultChannel.Writer.TryComplete();
        }, CancellationToken.None);

        await foreach (var marketData in resultChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return marketData;
        }
    }

    private List<Task> CreateWorkers(
        ChannelReader<ScrapeWorkItem> workReader,
        ChannelWriter<ScrapeWorkItem> workWriter,
        ChannelWriter<MarketCardData> resultWriter,
        IReadOnlyList<Proxy> proxies,
        ScrapingOptions scrapingOptions,
        Func<bool> markWorkItemCompleted,
        CancellationToken cancellationToken)
    {
        var workers = new List<Task>();

        var maxProxyWorkers = Math.Max(0, scrapingOptions.MaxConcurrentWorkers - 1);
        var proxiesToUse = proxies.Take(maxProxyWorkers).ToList();

        workers.Add(RunWorkerAsync(
            workReader,
            workWriter,
            resultWriter,
            markWorkItemCompleted,
            proxy: null,
            useChromium: true,
            canRetireWorker: false,
            scrapingOptions,
            cancellationToken));

        for (var i = 0; i < proxiesToUse.Count; i++)
        {
            var proxy = proxiesToUse[i];
            var useChromium = i % 2 == 0;

            workers.Add(RunWorkerAsync(
                workReader,
                workWriter,
                resultWriter,
                markWorkItemCompleted,
                proxy,
                useChromium,
                canRetireWorker: true,
                scrapingOptions,
                cancellationToken));
        }

        return workers;
    }

    private async Task RunWorkerAsync(
        ChannelReader<ScrapeWorkItem> workReader,
        ChannelWriter<ScrapeWorkItem> workWriter,
        ChannelWriter<MarketCardData> resultWriter,
        Func<bool> markWorkItemCompleted,
        Proxy? proxy,
        bool useChromium,
        bool canRetireWorker,
        ScrapingOptions scrapingOptions,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        try
        {
            await foreach (var workItem in workReader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var marketData = await ScrapeAsync(
                        workItem.Target,
                        proxy,
                        useChromium,
                        scrapingOptions,
                        cancellationToken);

                    consecutiveFailures = 0;

                    await resultWriter.WriteAsync(marketData, cancellationToken);

                    if (markWorkItemCompleted())
                    {
                        workWriter.TryComplete();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    consecutiveFailures++;

                    if (workItem.Attempt < MaxScrapeAttemptsPerTarget)
                    {
                        await workWriter.WriteAsync(new ScrapeWorkItem
                        {
                            Target = workItem.Target,
                            Attempt = workItem.Attempt + 1
                        }, cancellationToken);
                    }
                    else if (markWorkItemCompleted())
                    {
                        workWriter.TryComplete();
                    }

                    if (canRetireWorker && consecutiveFailures >= MaxConsecutiveWorkerFailures)
                    {
                        return;
                    }
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task<PlaywrightSession> CreateSessionAsync(
        bool useChromium,
        Proxy? proxy,
        bool headless,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (useChromium)
        {
            return await _playwrightSessionFactory.CreateChromiumAsync(headless, proxy);
        }

        return await _playwrightSessionFactory.CreateFirefoxAsync(headless, proxy);
    }

    private async Task<ScrapingOptions> BuildScrapingOptionsAsync(CancellationToken cancellationToken)
    {
        var settings = await _appSettingsService.GetCurrentAsync(cancellationToken);

        return new ScrapingOptions
        {
            Headless = settings.Scraping.Headless,
            MaxConcurrentWorkers = Math.Max(1, settings.Scraping.MaxConcurrentWorkers),
            CardmarketUsername = settings.Scraping.CardmarketUsername,
            CardmarketPassword = settings.Scraping.CardmarketPassword,
            UrlProxyCecker = settings.Scraping.UrlProxyChecker,
            SellerCountry = settings.Scraping.SellerCountry,
            Languages = settings.Scraping.Languages,
            MinCondition = settings.Scraping.MinCondition,
            Proxies = settings.Scraping.Proxies.Select(proxy => new ProxyOptions
            {
                Server = proxy.Server,
                Username = proxy.Username,
                Password = proxy.Password
            }).ToList(),
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

                    break;
                }

                await button.ClickAsync();
                clickTries = 0;
                await Task.Delay(WaitTiming.GetOneSecDiff(LoadMoreRowsWait));

                var lastPrice = await GetLastPriceAsync(page);
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
            Country = country ?? string.Empty,
            Price = price,
            AvailableQuantity = quantity,
            CardName = target.CardName,
            SetName = target.SetName,
            ProductUrl = target.ProductUrl
        };
    }

    private sealed class ScrapeWorkItem
    {
        public required ScrapingTarget Target { get; init; }
        public int Attempt { get; init; }
    }
}
