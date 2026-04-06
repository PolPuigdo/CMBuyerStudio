using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Infrastructure.Cardmarket.Builders;
using CMBuyerStudio.Infrastructure.Cardmarket.Parsing;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using CMBuyerStudio.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping
{
    public class CardMarketScraper : ICardMarketScraper
    {
        private readonly PlaywrightBuilder _playwrightBuilder;
        private readonly ScrapingOptions _scrapingOptions;

        private const decimal PriceMaxNum = 0.50m;
        private const decimal PriceMaxPercent = 10m;
        private const int LoadMoreMaxClicks = 10;
        private const string OfferRowSelector = ".article-table .table-body .article-row";
        private const int LoadMoreRowsTimeoutMs = 3500;
        private const int LoadMoreRowsPollMs = 150;

        public CardMarketScraper(PlaywrightBuilder playwrightBuilder, IOptions<ScrapingOptions> scrapingOptions)
        {
            _playwrightBuilder = playwrightBuilder;
            _scrapingOptions = scrapingOptions.Value;
        }

        public Task<MarketCardData> ScrapeAsync(ScrapingTarget target, CancellationToken cancellationToken = default)
        {
            Proxy? proxy = null;

            if (_scrapingOptions.Proxies is { Count: > 0 })
            {
                proxy = ToPlaywrightProxy(_scrapingOptions.Proxies[0]);
            }

            return ScrapeAsync(target, proxy, useChromium: true, cancellationToken);
        }

        private async Task<MarketCardData> ScrapeAsync(
            ScrapingTarget target,
            Proxy? proxy,
            bool useChromium,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);

            await using var session = await CreateSessionAsync(useChromium, proxy, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var filteredUrl = BuildFilteredCardUrl(target.ProductUrl);

            await session.Page.GotoAsync(filteredUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });

            await session.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await ExpandResultsAsync(session.Page);

            var firstPrice = await GetFirstPriceAsync(session.Page);
            var maxPrice = CalculateMaxPrice(firstPrice);

            var offers = await CollectOffersWithinMaxPriceAsync(
                session.Page,
                target,
                maxPrice);

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
            var workers = new List<Task>();

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

                return workers;
            }

            workers.Add(RunWorkerAsync(
                targetQueue,
                writer,
                proxy: null,
                useChromium: true,
                cancellationToken));

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
            var priceLocator = page
                .Locator(".article-table .table-body .article-row")
                .First
                .Locator(".col-offer .price-container span.color-primary");

            await priceLocator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible
            });

            var rawText = await priceLocator.InnerTextAsync();

            return CardmarketValueParsers.TryParseEuroPrice(rawText, out decimal firstPrice)
                ? firstPrice
                : 0m;
        }

        private static async Task ExpandResultsAsync(IPage page)
        {
            for (var click = 0; click < LoadMoreMaxClicks; click++)
            {
                var loadMoreButton = page.Locator("#loadMoreButton");
                if (await loadMoreButton.CountAsync() == 0)
                {
                    break;
                }

                var button = loadMoreButton.First;

                try
                {
                    if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync())
                    {
                        break;
                    }

                    var rowCountBeforeClick = await page.Locator(OfferRowSelector).CountAsync();

                    await button.ClickAsync();

                    var rowsExpanded = await WaitForRowsExpansionAfterLoadMoreAsync(page, rowCountBeforeClick);
                    if (!rowsExpanded)
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

        private static async Task<bool> WaitForRowsExpansionAfterLoadMoreAsync(IPage page, int previousRowCount)
        {
            var rows = page.Locator(OfferRowSelector);
            var loadMoreButton = page.Locator("#loadMoreButton");
            var start = DateTime.UtcNow;

            while ((DateTime.UtcNow - start).TotalMilliseconds < LoadMoreRowsTimeoutMs)
            {
                if (await rows.CountAsync() > previousRowCount)
                {
                    return true;
                }

                if (await loadMoreButton.CountAsync() == 0)
                {
                    return false;
                }

                var button = loadMoreButton.First;
                if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync())
                {
                    return false;
                }

                await Task.Delay(LoadMoreRowsPollMs);
            }

            return await rows.CountAsync() > previousRowCount;
        }

        private async Task<IReadOnlyList<SellerOffer>> CollectOffersWithinMaxPriceAsync(
    IPage page,
    ScrapingTarget target,
    decimal maxPrice)
        {
            var offers = new List<SellerOffer>();
            const int maxPagesToScan = 50;

            for (var currentPage = 0; currentPage < maxPagesToScan; currentPage++)
            {
                var rows = page.Locator(OfferRowSelector);

                await rows.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible
                });

                var rowCount = await rows.CountAsync();
                var reachedPriceLimit = false;

                for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var offer = await TryExtractOfferAsync(rows.Nth(rowIndex), target);
                    if (offer is null)
                    {
                        continue;
                    }

                    if (offer.Price > maxPrice)
                    {
                        reachedPriceLimit = true;
                        break;
                    }

                    offers.Add(offer);
                }

                if (reachedPriceLimit)
                {
                    break;
                }

                if (!await TryGoToNextPageAsync(page))
                {
                    break;
                }
            }

            return offers;
        }

        private static async Task<bool> TryGoToNextPageAsync(IPage page)
        {
            var nextPageSelectors = new[]
            {
        "ul.pagination li.next a",
        "ul.pagination li.page-item.next a",
        "a[rel='next']",
        "a[aria-label='Next page']"
    };

            foreach (var selector in nextPageSelectors)
            {
                var buttons = page.Locator(selector);
                var count = await buttons.CountAsync();

                for (var i = 0; i < count; i++)
                {
                    var button = buttons.Nth(i);
                    if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync())
                    {
                        continue;
                    }

                    var classes = await button.GetAttributeAsync("class") ?? string.Empty;
                    var parentClasses = await button.EvaluateAsync<string?>("el => el.parentElement?.className") ?? string.Empty;

                    if (classes.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                        || parentClasses.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var previousUrl = page.Url;
                    var previousRowsSnapshot = await BuildRowsSnapshotAsync(page);

                    await button.ClickAsync();

                    return await WaitForPaginationChangeAsync(page, previousUrl, previousRowsSnapshot);
                }
            }

            return false;
        }

        private static async Task<bool> WaitForPaginationChangeAsync(
            IPage page,
            string previousUrl,
            string previousRowsSnapshot)
        {
            var start = DateTime.UtcNow;

            while ((DateTime.UtcNow - start).TotalMilliseconds < LoadMoreRowsTimeoutMs)
            {
                if (!string.Equals(page.Url, previousUrl, StringComparison.Ordinal))
                {
                    return true;
                }

                var currentRowsSnapshot = await BuildRowsSnapshotAsync(page);
                if (!string.Equals(currentRowsSnapshot, previousRowsSnapshot, StringComparison.Ordinal))
                {
                    return true;
                }

                await Task.Delay(LoadMoreRowsPollMs);
            }

            return false;
        }

        private static async Task<string> BuildRowsSnapshotAsync(IPage page)
        {
            var rows = page.Locator(OfferRowSelector);
            var rowCount = await rows.CountAsync();
            if (rowCount == 0)
            {
                return string.Empty;
            }

            var take = Math.Min(3, rowCount);
            var snapshot = new List<string>(take);

            for (var i = 0; i < take; i++)
            {
                var rowText = await rows.Nth(i).InnerTextAsync();
                snapshot.Add(NormalizeWhitespace(rowText));
            }

            return string.Join(" || ", snapshot);
        }

        private static async Task<SellerOffer?> TryExtractOfferAsync(ILocator row, ScrapingTarget target)
        {
            if (!await IsPurchasableAsync(row))
            {
                return null;
            }

            if (await HasNonCertifiedShippingWarningAsync(row))
            {
                return null;
            }

            var (sellerName, _) = await GetSellerInfoAsync(row);
            if (string.IsNullOrWhiteSpace(sellerName))
            {
                return null;
            }

            var priceText = await GetFirstNonEmptyTextAsync(row, new[]
            {
                ".col-offer .price-container span.color-primary",
                ".col-offer .price-container",
                ".col-offer"
            });

            if (!CardmarketValueParsers.TryParseEuroPrice(priceText, out decimal price))
            {
                return null;
            }

            var quantity = await TryExtractQuantityAsync(row);
            if (quantity <= 0)
            {
                quantity = 1;
            }

            var country = await GetCountryAsync(row);

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

        private static async Task<bool> IsPurchasableAsync(ILocator row)
        {
            var buyButton = row.Locator(
                "button[data-id-amount], button[aria-label*='Carrito'], button[aria-label*='cart']");
            var count = await buyButton.CountAsync();

            for (var index = 0; index < count; index++)
            {
                var candidate = buyButton.Nth(index);
                if (!await candidate.IsVisibleAsync() || !await candidate.IsEnabledAsync())
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static async Task<bool> HasNonCertifiedShippingWarningAsync(ILocator row)
        {
            var candidates = row.Locator("[aria-label], [data-bs-original-title], [data-original-title], [title]");
            var count = await candidates.CountAsync();

            for (var i = 0; i < count; i++)
            {
                var candidate = candidates.Nth(i);
                foreach (var attributeName in new[] { "aria-label", "data-bs-original-title", "data-original-title", "title" })
                {
                    var value = await candidate.GetAttributeAsync(attributeName);
                    if (IsNonCertifiedShippingWarningText(value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNonCertifiedShippingWarningText(string? raw)
            => OfferTextParser.IsNonCertifiedShippingWarningText(raw);

        private static async Task<(string? SellerName, string SellerProfileUrl)> GetSellerInfoAsync(ILocator row)
        {
            var sellerLinks = row.Locator(".col-seller .seller-name a[href], .col-seller a[href*='/Users/']");
            var count = await sellerLinks.CountAsync();

            for (var index = 0; index < count; index++)
            {
                var link = sellerLinks.Nth(index);
                var sellerName = (await link.InnerTextAsync()).Trim();
                if (string.IsNullOrWhiteSpace(sellerName))
                {
                    continue;
                }

                var href = await link.GetAttributeAsync("href");
                return (NormalizeWhitespace(sellerName), BuildAbsoluteUrl(row.Page.Url, href));
            }

            var fallbackName = await GetFirstNonEmptyTextAsync(row, new[]
            {
                ".col-seller .seller-name a",
                ".col-seller .seller-name",
                ".col-seller a",
                ".seller-name",
                "a[href*='/Users/']"
            });

            return (fallbackName, string.Empty);
        }

        private static string BuildAbsoluteUrl(string pageUrl, string? href)
            => CardmarketValueParsers.BuildAbsoluteUrl(pageUrl, href);

        private static async Task<int> TryExtractQuantityAsync(ILocator row)
        {
            var quantityText = await GetFirstNonEmptyTextAsync(row, new[]
            {
                ".col-offer .item-count",
                ".col-offer .amount",
                ".col-offer"
            });

            if (string.IsNullOrWhiteSpace(quantityText))
            {
                return 0;
            }

            var integerMatches = Regex.Matches(quantityText, @"\d+");
            if (integerMatches.Count == 0)
            {
                return 0;
            }

            return int.TryParse(integerMatches[^1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)
                ? quantity
                : 0;
        }

        private static async Task<string?> GetFirstNonEmptyTextAsync(ILocator root, IEnumerable<string> selectors)
        {
            foreach (var selector in selectors)
            {
                var locator = root.Locator(selector);
                var count = await locator.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    var candidate = locator.Nth(i);
                    var text = (await candidate.InnerTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return NormalizeWhitespace(text);
                    }
                }
            }

            return null;
        }

        private static async Task<string?> GetFirstAttributeAsync(ILocator root, string selector, string attributeName)
        {
            var locator = root.Locator(selector);
            var count = await locator.CountAsync();

            for (var i = 0; i < count; i++)
            {
                var value = await locator.Nth(i).GetAttributeAsync(attributeName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return NormalizeWhitespace(value);
                }
            }

            return null;
        }

        private static string NormalizeWhitespace(string value)
            => Regex.Replace(value, @"\s+", " ").Trim();

        private static async Task<string> GetCountryAsync(ILocator row)
        {
            var countryFromTooltip = await TryGetCountryFromTooltipAsync(row);
            if (!string.IsNullOrWhiteSpace(countryFromTooltip))
            {
                return countryFromTooltip;
            }

            var countryFromFlag = await GetFirstAttributeAsync(
                    row,
                    ".col-seller img, .col-seller [class*='flag'], .seller-name img, .seller-name [class*='flag']",
                    "title")
                ?? await GetFirstAttributeAsync(
                    row,
                    ".col-seller img, .col-seller [class*='flag'], .seller-name img, .seller-name [class*='flag']",
                    "alt");

            var parsedFromFlag = ParseCountryFromLocationLabel(countryFromFlag);
            if (!string.IsNullOrWhiteSpace(parsedFromFlag))
            {
                return parsedFromFlag;
            }

            return "Unknown";
        }

        private static async Task<string?> TryGetCountryFromTooltipAsync(ILocator row)
        {
            var candidates = row.Locator(
                ".col-seller [aria-label], .col-seller [data-bs-original-title], .col-seller [data-original-title], .seller-name [aria-label], .seller-name [data-bs-original-title], .seller-name [data-original-title], [aria-label*='location'], [data-bs-original-title*='location'], [aria-label*='ubicaci'], [data-bs-original-title*='ubicaci']");

            var count = await candidates.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var candidate = candidates.Nth(i);
                foreach (var attributeName in new[] { "aria-label", "data-bs-original-title", "data-original-title", "title", "alt" })
                {
                    var value = await candidate.GetAttributeAsync(attributeName);
                    var parsed = ParseCountryFromLocationLabel(value);
                    if (!string.IsNullOrWhiteSpace(parsed))
                    {
                        return parsed;
                    }
                }
            }

            return null;
        }

        private static string? ParseCountryFromLocationLabel(string? value)
            => OfferTextParser.ParseCountryFromLocationLabel(value);

        private string BuildFilteredCardUrl(string baseUrl)
        {
            var spanishUrl = EnsureSpanishLocaleUrl(baseUrl);

            if (!Uri.TryCreate(spanishUrl, UriKind.Absolute, out var uri))
            {
                return spanishUrl;
            }

            var query = ParseQuery(uri.Query);

            if (!string.IsNullOrWhiteSpace(_scrapingOptions.SellerCountry))
            {
                query["sellerCountry"] = _scrapingOptions.SellerCountry;
            }

            if (!string.IsNullOrWhiteSpace(_scrapingOptions.Languages))
            {
                query["language"] = _scrapingOptions.Languages;
            }

            query["minCondition"] = _scrapingOptions.MinCondition.ToString(CultureInfo.InvariantCulture);

            var builder = new UriBuilder(uri)
            {
                Query = BuildQuery(query)
            };

            return builder.Uri.ToString();
        }

        private static string EnsureSpanishLocaleUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return url;
            }

            if (!uri.Host.Contains("cardmarket.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.ToString();
            }

            var path = uri.AbsolutePath;
            if (Regex.IsMatch(path, @"^/[a-z]{2}(?=/|$)", RegexOptions.IgnoreCase))
            {
                path = Regex.Replace(path, @"^/[a-z]{2}(?=/|$)", "/es", RegexOptions.IgnoreCase);
            }
            else
            {
                path = path.StartsWith("/", StringComparison.Ordinal)
                    ? $"/es{path}"
                    : $"/es/{path}";
            }

            var builder = new UriBuilder(uri)
            {
                Path = path
            };

            return builder.Uri.ToString();
        }

        private static string BuildQuery(Dictionary<string, string> query)
        {
            if (query.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
            {
                return result;
            }

            var trimmed = query.TrimStart('?');
            foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split('=', 2);
                var key = Uri.UnescapeDataString(pieces[0]);
                var value = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = value;
                }
            }

            return result;
        }
    }
}
