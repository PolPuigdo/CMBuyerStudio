using CMBuyerStudio.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Helpers
{
    public static class UrlBuilder
    {
        public static string SearchUrl(string query)
        {
            var trimmedQuery = (query ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(trimmedQuery))
            {
                throw new ArgumentException("Search query is required.", nameof(query));
            }

            var encodedQuery = Uri.EscapeDataString(trimmedQuery).Replace("%20", "+", StringComparison.Ordinal);
            return $"https://www.cardmarket.com/en/Magic/Products/Singles?idExpansion=0&searchString={encodedQuery}&mode=list";
        }

        public static string BuildFilteredCardUrl(string baseUrl, ScrapingOptions scrapingOptions)
        {
            var spanishUrl = EnsureSpanishLocaleUrl(baseUrl);

            if (!Uri.TryCreate(spanishUrl, UriKind.Absolute, out var uri))
            {
                return spanishUrl;
            }

            var query = ParseQuery(uri.Query);

            if (!string.IsNullOrWhiteSpace(scrapingOptions.SellerCountry))
            {
                query["sellerCountry"] = scrapingOptions.SellerCountry;
            }

            if (!string.IsNullOrWhiteSpace(scrapingOptions.Languages))
            {
                query["language"] = scrapingOptions.Languages;
            }

            query["minCondition"] = scrapingOptions.MinCondition.ToString(CultureInfo.InvariantCulture);

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

        private static string BuildQuery(Dictionary<string, string> query)
        {
            if (query.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }
    }
}
