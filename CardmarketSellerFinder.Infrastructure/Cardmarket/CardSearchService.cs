using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.Search;
using CMBuyerStudio.Infrastructure.Cardmarket.Builders;
using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using Microsoft.Playwright;

namespace CMBuyerStudio.Application.Services
{
    public class CardSearchService : ICardSearchService
    {
        private readonly PlaywrightBuilder _playwrightBuilder;
        private readonly PlaywrightParser _playwrightParser;
        private readonly ICardImageCacheService _imageCacheService;

        public CardSearchService(PlaywrightBuilder playwrightBuilder, PlaywrightParser playwrightParser, ICardImageCacheService imageCacheService)
        {
            _playwrightBuilder = playwrightBuilder ?? throw new ArgumentNullException(nameof(playwrightBuilder));
            _playwrightParser = playwrightParser ?? throw new ArgumentNullException(nameof(playwrightParser));
            _imageCacheService = imageCacheService ?? throw new ArgumentNullException(nameof(imageCacheService));
        }

        public async Task<IReadOnlyList<SearchCardResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            var searchUrl = UrlBuilder.SearchUrl(query);
            var playwright = await _playwrightBuilder.CreateChromiumAsync();
            var page = playwright.Page;

            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            });

            await page.WaitForTimeoutAsync(500);

            var rows = page.Locator(".table-body > div[id^='productRow']");
            var rowCount = await rows.CountAsync();

            var cards = new Dictionary<string, SearchCardResult>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < rowCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parsed = await TryParseRowAsync(rows.Nth(index), page.Url);
                if (parsed is null)
                {
                    continue;
                }

                if (cards.ContainsKey(parsed.ProductUrl))
                {
                    continue;
                }

                var imageName = parsed.CardName+"_"+parsed.SetName;

                cards[parsed.ProductUrl] = parsed;
            }

            // image download loop
            parsed.ImagePath = await _imageCacheService.GetOrDownloadAsync(parsed.ImageUrl, imageName);

            await playwright.DisposeAsync();

            return cards.Values
                .OrderBy(item => item.Price)
                .ThenBy(item => item.CardName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<SearchCardResult?> TryParseRowAsync(ILocator row, string searchUrl)
        {
            try
            {
                var rowHtml = await row.InnerHTMLAsync();
                return _playwrightParser.ParseSearchCardResultAsync(rowHtml, searchUrl);
            }
            catch (Exception ex)
            {
                // Log the exception if necessary
                Console.WriteLine($"Error parsing row: {ex.Message}");
                return null;
            }
        }
    }
}
