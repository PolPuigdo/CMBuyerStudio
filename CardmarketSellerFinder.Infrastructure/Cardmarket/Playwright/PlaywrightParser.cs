using CMBuyerStudio.Domain.Search;
using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright
{
    public partial class PlaywrightParser
    {
        public static bool TryParseEuroPrice(string? rawText, out decimal price) => ValueParsers.TryParseEuroPrice(rawText, out price);
        public static string BuildAbsoluteUrl(string pageUrl, string? href) => ValueParsers.BuildAbsoluteUrl(pageUrl, href);
        public PlaywrightParser() { }

        public SearchCardResult? ParseSearchCardResultAsync(string row, string currentUrl)
        {
            if (string.IsNullOrWhiteSpace(row))
            {
                return null;
            }

            var nameMatch = NameBlockRegex().Match(row);
            if (!nameMatch.Success)
            {
                return null;
            }

            var rawName = HtmlDecodeAndStrip(nameMatch.Groups["name"].Value);
            var href = WebUtility.HtmlDecode(nameMatch.Groups["href"].Value);
            var productUrl = BuildAbsoluteUrl(currentUrl, href);

            if (string.IsNullOrWhiteSpace(rawName) || string.IsNullOrWhiteSpace(productUrl))
            {
                return null;
            }

            var setName = ExtractSetName(row);

            var priceMatch = PriceBlockRegex().Match(row);
            if (!priceMatch.Success)
            {
                return null;
            }

            var priceText = HtmlDecodeAndStrip(priceMatch.Groups["price"].Value);
            if (!TryParseEuroPrice(priceText, out var fromPrice))
            {
                return null;
            }

            var previewMatch = PreviewTooltipRegex().Match(row);
            var tooltipHtml = previewMatch.Success
                ? WebUtility.HtmlDecode(previewMatch.Groups["tooltip"].Value)
                : string.Empty;

            var imageUrl = ExtractImageUrlFromTooltipHtml(tooltipHtml);

            return new SearchCardResult
            {
                CardName = rawName,
                SetName = setName,
                ProductUrl = productUrl,
                Price = fromPrice,
                ImageUrl = imageUrl
            };
        }

        private static string HtmlDecodeAndStrip(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var decoded = WebUtility.HtmlDecode(value);
            decoded = HtmlTagRegex().Replace(decoded, string.Empty);
            return NormalizeWhitespaceRegex().Replace(decoded, " ").Trim();
        }

        private static string ExtractSetName(string rowHtml)
        {
            var expansionBlockMatch = ExpansionBlockRegex().Match(rowHtml);
            if (!expansionBlockMatch.Success)
            {
                return string.Empty;
            }

            var expansionBlock = expansionBlockMatch.Groups["block"].Value;

            var ariaLabelMatch = AriaLabelRegex().Match(expansionBlock);
            if (ariaLabelMatch.Success)
            {
                return HtmlDecodeAndStrip(ariaLabelMatch.Groups["value"].Value);
            }

            var tooltipTitleMatch = TooltipTitleRegex().Match(expansionBlock);
            if (tooltipTitleMatch.Success)
            {
                return HtmlDecodeAndStrip(tooltipTitleMatch.Groups["value"].Value);
            }

            return string.Empty;
        }

        public static string ExtractImageUrlFromTooltipHtml(string? tooltipHtml)
        {
            if (string.IsNullOrWhiteSpace(tooltipHtml))
            {
                return string.Empty;
            }

            var decoded = WebUtility.HtmlDecode(tooltipHtml);
            var imageMatch = TooltipImageRegex().Match(decoded);
            if (!imageMatch.Success)
            {
                return string.Empty;
            }

            var src = imageMatch.Groups["src"].Value.Trim();
            if (string.IsNullOrWhiteSpace(src))
            {
                return string.Empty;
            }

            if (src.StartsWith("//", StringComparison.Ordinal))
            {
                src = $"https:{src}";
            }

            return src;
        }

        [GeneratedRegex("data-testid\\s*=\\s*['\\\"]name['\\\"][\\s\\S]*?<a[^>]*href\\s*=\\s*['\\\"](?<href>[^'\\\"]+)['\\\"][^>]*>(?<name>[\\s\\S]*?)</a>", RegexOptions.IgnoreCase)]
        private static partial Regex NameBlockRegex();

        [GeneratedRegex("<img[^>]*src\\s*=\\s*['\\\"](?<src>[^'\\\"]+)['\\\"]", RegexOptions.IgnoreCase)]
        private static partial Regex TooltipImageRegex();

        [GeneratedRegex("data-testid\\s*=\\s*['\\\"]from_price['\\\"][^>]*>(?<price>[\\s\\S]*?)</", RegexOptions.IgnoreCase)]
        private static partial Regex PriceBlockRegex();

        [GeneratedRegex("data-testid\\s*=\\s*['\\\"]preview['\\\"][\\s\\S]*?data-bs-title\\s*=\\s*['\\\"](?<tooltip>[^'\\\"]*)['\\\"]", RegexOptions.IgnoreCase)]
        private static partial Regex PreviewTooltipRegex();

        [GeneratedRegex("data-testid\\s*=\\s*['\\\"]expansion['\\\"][\\s\\S]*?(?<block><a[\\s\\S]*?</a>)", RegexOptions.IgnoreCase)]
        private static partial Regex ExpansionBlockRegex();

        [GeneratedRegex("aria-label\\s*=\\s*['\\\"](?<value>[^'\\\"]+)['\\\"]", RegexOptions.IgnoreCase)]
        private static partial Regex AriaLabelRegex();

        [GeneratedRegex("data-bs-original-title\\s*=\\s*['\\\"](?<value>[^'\\\"]+)['\\\"]", RegexOptions.IgnoreCase)]
        private static partial Regex TooltipTitleRegex();

        [GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase)]
        private static partial Regex HtmlTagRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex NormalizeWhitespaceRegex();
    }
}
