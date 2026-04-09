using System.Globalization;
using System.Text;

namespace CMBuyerStudio.Application.Common.Countries;

public static class CountryCatalog
{
    private static readonly HashSet<string> KnownCountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "ES", "FI", "FR", "DE", "GR", "HU",
        "IE", "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "SE"
    };

    private static readonly Dictionary<string, string> AliasToIso2 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALEMANIA"] = "DE",
        ["GERMANY"] = "DE",

        ["AUSTRIA"] = "AT",

        ["BELGICA"] = "BE",
        ["BELGIUM"] = "BE",

        ["BULGARIA"] = "BG",

        ["CROACIA"] = "HR",
        ["CROATIA"] = "HR",

        ["CHIPRE"] = "CY",
        ["CYPRUS"] = "CY",

        ["DINAMARCA"] = "DK",
        ["DENMARK"] = "DK",

        ["ESLOVAQUIA"] = "SK",
        ["SLOVAKIA"] = "SK",

        ["ESLOVENIA"] = "SI",
        ["SLOVENIA"] = "SI",

        ["ESTONIA"] = "EE",

        ["ESPANA"] = "ES",
        ["ESPANYA"] = "ES",
        ["SPAIN"] = "ES",

        ["FINLANDIA"] = "FI",
        ["FINLAND"] = "FI",
        ["FINDLAND"] = "FI",

        ["FRANCIA"] = "FR",
        ["FRANCE"] = "FR",

        ["GRECIA"] = "GR",
        ["GREECE"] = "GR",

        ["HOLANDA"] = "NL",
        ["NETHERLANDS"] = "NL",

        ["HUNGRIA"] = "HU",
        ["HUNGARY"] = "HU",

        ["IRLANDA"] = "IE",
        ["IRELAND"] = "IE",

        ["ITALIA"] = "IT",
        ["ITALY"] = "IT",

        ["LETONIA"] = "LV",
        ["LATVIA"] = "LV",

        ["LITUANIA"] = "LT",
        ["LITHUANIA"] = "LT",

        ["LUXEMBURGO"] = "LU",
        ["LUXEMBOURG"] = "LU",

        ["MALTA"] = "MT",

        ["POLONIA"] = "PL",
        ["POLAND"] = "PL",

        ["PORTUGAL"] = "PT",

        ["RUMANIA"] = "RO",
        ["ROMANIA"] = "RO",

        ["REPUBLICACHECA"] = "CZ",
        ["CZECHREPUBLIC"] = "CZ",

        ["SUECIA"] = "SE",
        ["SWEDEN"] = "SE",

        ["REPUBLICACHECA"] = "CZ",
        ["CZECHREPUBLIC"] = "CZ",
        ["PAISESBAJOS"] = "NL",
        ["THE NETHERLANDS"] = "NL"
    };

    public static IReadOnlyCollection<string> DefaultEuCountryCodes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
            "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE"
        };

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

        if (AliasToIso2.TryGetValue(normalized, out var aliasCode))
        {
            countryCode = aliasCode;
            return true;
        }

        return false;
    }

    private static string NormalizeForMatch(string? value)
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
}