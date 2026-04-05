using System;
using System.Collections.Generic;
using System.Text;

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
    }
}
