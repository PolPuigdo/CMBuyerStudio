using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Helpers
{
    public static class ValueParsers
    {
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
    }
}
