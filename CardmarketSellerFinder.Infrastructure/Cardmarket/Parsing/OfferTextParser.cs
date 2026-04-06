using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Parsing
{
    public static class OfferTextParser
    {
        public static bool IsNonCertifiedShippingWarningText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var decoded = WebUtility.HtmlDecode(raw);
            decoded = Regex.Replace(decoded, "<.*?>", string.Empty);
            decoded = NormalizeWhitespace(decoded);

            if (string.IsNullOrWhiteSpace(decoded))
            {
                return false;
            }

            var normalized = CountryCatalog.NormalizeForMatch(decoded);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            var hasShippingToken = normalized.Contains("ENVIO", StringComparison.Ordinal)
                || normalized.Contains("SHIPPING", StringComparison.Ordinal);
            if (!hasShippingToken)
            {
                return false;
            }

            if (!normalized.Contains("CERTIFIC", StringComparison.Ordinal))
            {
                return false;
            }

            return normalized.Contains("SINCERTIFIC", StringComparison.Ordinal)
                || normalized.Contains("NOCERTIFIC", StringComparison.Ordinal)
                || normalized.Contains("UNCERTIFIED", StringComparison.Ordinal);
        }

        public static string? ParseCountryFromLocationLabel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var decoded = WebUtility.HtmlDecode(value);
            decoded = Regex.Replace(decoded, "<.*?>", string.Empty);
            decoded = NormalizeWhitespace(decoded);

            var match = Regex.Match(
                decoded,
                @"(?:item\s+location|article\s+location|ubicaci[oó]n\s+del\s+art[ií]culo|localizaci[oó]n\s+del\s+art[ií]culo)\s*:\s*(.+)",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                var parts = decoded.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && IsLocationLabelPrefix(parts[0]))
                {
                    return NormalizeWhitespace(parts[1]);
                }

                if (CountryCatalog.TryGetCountryCode(decoded, out _))
                {
                    return decoded;
                }

                return null;
            }

            return NormalizeWhitespace(match.Groups[1].Value);
        }

        public static bool IsLocationLabelPrefix(string value)
        {
            var normalized = CountryCatalog.NormalizeForMatch(value);
            return normalized.Contains("ITEMLOCATION", StringComparison.Ordinal)
                || normalized.Contains("ARTICLELOCATION", StringComparison.Ordinal)
                || normalized.Contains("UBICACIONDELARTICULO", StringComparison.Ordinal)
                || normalized.Contains("LOCALIZACIONDELARTICULO", StringComparison.Ordinal);
        }

        private static string NormalizeWhitespace(string value)
            => Regex.Replace(value, @"\s+", " ").Trim();
    }

}
