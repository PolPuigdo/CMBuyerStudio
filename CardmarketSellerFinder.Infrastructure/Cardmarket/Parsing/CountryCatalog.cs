using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Parsing
{
    public static class CountryCatalog
    {
        private static readonly HashSet<string> KnownCountryCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "AT", "BE", "BG", "CA", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GB", "GR", "HR",
            "HU", "IE", "IS", "IT", "JP", "LI", "LT", "LU", "LV", "MT", "NL", "NO", "PL", "PT", "RO", "SE",
            "SG", "SI", "SK"
        };

        private static readonly Dictionary<string, string> Iso3ToIso2 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AUT"] = "AT",
            ["BEL"] = "BE",
            ["BGR"] = "BG",
            ["CAN"] = "CA",
            ["CHE"] = "CH",
            ["CYP"] = "CY",
            ["CZE"] = "CZ",
            ["DEU"] = "DE",
            ["DNK"] = "DK",
            ["EST"] = "EE",
            ["ESP"] = "ES",
            ["FIN"] = "FI",
            ["FRA"] = "FR",
            ["GBR"] = "GB",
            ["GRC"] = "GR",
            ["HRV"] = "HR",
            ["HUN"] = "HU",
            ["IRL"] = "IE",
            ["ISL"] = "IS",
            ["ITA"] = "IT",
            ["JPN"] = "JP",
            ["LIE"] = "LI",
            ["LTU"] = "LT",
            ["LUX"] = "LU",
            ["LVA"] = "LV",
            ["MLT"] = "MT",
            ["NLD"] = "NL",
            ["NOR"] = "NO",
            ["POL"] = "PL",
            ["PRT"] = "PT",
            ["ROU"] = "RO",
            ["SWE"] = "SE",
            ["SGP"] = "SG",
            ["SVN"] = "SI",
            ["SVK"] = "SK"
        };

        private static readonly Dictionary<string, string> AliasToIso2 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ALEMANIA"] = "DE",
            ["AUSTRIA"] = "AT",
            ["BULGARIA"] = "BG",
            ["BELGICA"] = "BE",
            ["BELGIUM"] = "BE",
            ["CANADA"] = "CA",
            ["CHIPRE"] = "CY",
            ["CYPRUS"] = "CY",
            ["CROACIA"] = "HR",
            ["CROATIA"] = "HR",
            ["DINAMARCA"] = "DK",
            ["DENMARK"] = "DK",
            ["ESLOVAQUIA"] = "SK",
            ["SLOVAKIA"] = "SK",
            ["ESLOVENIA"] = "SI",
            ["SLOVENIA"] = "SI",
            ["ESTONIA"] = "EE",
            ["FINLANDIA"] = "FI",
            ["FINLAND"] = "FI",
            ["FINDLAND"] = "FI",
            ["FRANCIA"] = "FR",
            ["FRANCE"] = "FR",
            ["GERMANY"] = "DE",
            ["GRECIA"] = "GR",
            ["GREECE"] = "GR",
            ["HOLANDA"] = "NL",
            ["NETHERLANDS"] = "NL",
            ["HUNGRIA"] = "HU",
            ["HUNGARY"] = "HU",
            ["IRLANDA"] = "IE",
            ["IRELAND"] = "IE",
            ["ISLANDIA"] = "IS",
            ["ICELAND"] = "IS",
            ["ITALIA"] = "IT",
            ["ITALY"] = "IT",
            ["JAPON"] = "JP",
            ["JAPAN"] = "JP",
            ["LETONIA"] = "LV",
            ["LATVIA"] = "LV",
            ["LIECHTENSTEIN"] = "LI",
            ["LITUANIA"] = "LT",
            ["LITHUANIA"] = "LT",
            ["LUXEMBURGO"] = "LU",
            ["LUXENBURGO"] = "LU",
            ["LUXEMBOURG"] = "LU",
            ["MALTA"] = "MT",
            ["NORUEGA"] = "NO",
            ["NORWAY"] = "NO",
            ["POLONIA"] = "PL",
            ["POLAND"] = "PL",
            ["PORTUGAL"] = "PT",
            ["REINOUNIDO"] = "GB",
            ["UNITEDKINGDOM"] = "GB",
            ["GREATBRITAIN"] = "GB",
            ["BRITAIN"] = "GB",
            ["ENGLAND"] = "GB",
            ["SCOTLAND"] = "GB",
            ["WALES"] = "GB",
            ["NORTHERNIRELAND"] = "GB",
            ["REPUBLICACHECA"] = "CZ",
            ["CZECHREPUBLIC"] = "CZ",
            ["CZECHIA"] = "CZ",
            ["RUMANIA"] = "RO",
            ["ROMANIA"] = "RO",
            ["SINGAPUR"] = "SG",
            ["SINGAPORE"] = "SG",
            ["SUECIA"] = "SE",
            ["SWEDEN"] = "SE",
            ["SUIZA"] = "CH",
            ["SWITZERLAND"] = "CH",
            ["ESPANA"] = "ES",
            ["ESPANYA"] = "ES",
            ["SPAIN"] = "ES"
        };

        private static readonly Dictionary<string, string> Iso2ToDisplayName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AT"] = "Austria",
            ["BE"] = "Belgium",
            ["BG"] = "Bulgaria",
            ["CA"] = "Canada",
            ["CH"] = "Switzerland",
            ["CY"] = "Cyprus",
            ["CZ"] = "Czech Republic",
            ["DE"] = "Germany",
            ["DK"] = "Denmark",
            ["EE"] = "Estonia",
            ["ES"] = "Spain",
            ["FI"] = "Finland",
            ["FR"] = "France",
            ["GB"] = "United Kingdom",
            ["GR"] = "Greece",
            ["HR"] = "Croatia",
            ["HU"] = "Hungary",
            ["IE"] = "Ireland",
            ["IS"] = "Iceland",
            ["IT"] = "Italy",
            ["JP"] = "Japan",
            ["LI"] = "Liechtenstein",
            ["LT"] = "Lithuania",
            ["LU"] = "Luxembourg",
            ["LV"] = "Latvia",
            ["MT"] = "Malta",
            ["NL"] = "Netherlands",
            ["NO"] = "Norway",
            ["PL"] = "Poland",
            ["PT"] = "Portugal",
            ["RO"] = "Romania",
            ["SE"] = "Sweden",
            ["SG"] = "Singapore",
            ["SI"] = "Slovenia",
            ["SK"] = "Slovakia"
        };

        public static IReadOnlyCollection<string> DefaultEuCountryCodes { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
                "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE"
            };

        public static string NormalizeForMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (var c in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToUpperInvariant(c));
                }
            }

            return builder.ToString();
        }

        public static bool TryGetCountryCode(string? value, out string countryCode)
        {
            countryCode = string.Empty;
            var normalized = NormalizeForMatch(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.Length == 2 && KnownCountryCodes.Contains(normalized))
            {
                countryCode = normalized;
                return true;
            }

            if (normalized.Length == 3 && Iso3ToIso2.TryGetValue(normalized, out var iso2))
            {
                countryCode = iso2;
                return true;
            }

            if (AliasToIso2.TryGetValue(normalized, out var aliasCountryCode))
            {
                countryCode = aliasCountryCode;
                return true;
            }

            return false;
        }

        public static string ToDisplayName(string? value)
        {
            if (!TryGetCountryCode(value, out var code))
            {
                return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
            }

            return Iso2ToDisplayName.TryGetValue(code, out var displayName)
                ? displayName
                : code;
        }
    }
}
