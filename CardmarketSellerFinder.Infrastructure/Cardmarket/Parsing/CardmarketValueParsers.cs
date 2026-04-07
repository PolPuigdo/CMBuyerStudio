using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Parsing
{
    public static class CardmarketValueParsers
    {
        private static bool IsNonCertifiedShippingWarningText(string? raw)
            => OfferTextParser.IsNonCertifiedShippingWarningText(raw);

        private static string NormalizeWhitespace(string value)
            => Regex.Replace(value, @"\s+", " ").Trim();

        private static string? ParseCountryFromLocationLabel(string? value)
            => OfferTextParser.ParseCountryFromLocationLabel(value);

        public static bool TryParseEuroPrice(string? rawText, out decimal price)
        {
            price = 0m;
            if (!TryNormalizePrice(rawText, out var normalized))
            {
                return false;
            }

            return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
        }

        public static bool TryParseEuroPrice(string? rawText, out double price)
        {
            price = 0d;
            if (!TryNormalizePrice(rawText, out var normalized))
            {
                return false;
            }

            return double.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
        }

        public static string BuildAbsoluteUrl(string pageUrl, string? href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return string.Empty;
            }

            if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }

            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)
                && Uri.TryCreate(baseUri, href, out var resolvedUri))
            {
                return resolvedUri.ToString();
            }

            if (href.StartsWith("/", StringComparison.Ordinal))
            {
                return $"https://www.cardmarket.com{href}";
            }

            return href;
        }

        private static bool TryNormalizePrice(string? rawText, out string normalized)
        {
            normalized = string.Empty;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                return false;
            }

            if (rawText.Contains("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var numericChars = rawText.Where(c => char.IsDigit(c) || c == '.' || c == ',');
            var sanitized = new string(numericChars.ToArray());
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return false;
            }

            normalized = sanitized
                .Replace(".", string.Empty, StringComparison.Ordinal)
                .Replace(",", ".", StringComparison.Ordinal);

            return true;
        }

        public static async Task<bool> IsPurchasableAsync(ILocator row)
        {
            var buyButton = row.Locator("button[data-id-amount], button[aria-label*='Carrito'], button[aria-label*='cart']");
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

        public static async Task<bool> HasNonCertifiedShippingWarningAsync(ILocator row)
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
        
        public static async Task<(string? SellerName, string SellerProfileUrl)> GetSellerInfoAsync(ILocator row)
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

        public static async Task<string?> GetFirstNonEmptyTextAsync(ILocator root, IEnumerable<string> selectors)
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

        public static async Task<int> TryExtractQuantityAsync(ILocator row)
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

        public static async Task<string?> TryGetCountryFromTooltipAsync(ILocator row)
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
    }
}
