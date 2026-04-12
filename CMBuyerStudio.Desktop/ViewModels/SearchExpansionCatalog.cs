using System.Reflection;
using System.Text.Json;

namespace CMBuyerStudio.Desktop.ViewModels;

internal static class SearchExpansionCatalog
{
    private const string ResourceSuffix = ".Data.expansiones_y_values.json";

    public static IReadOnlyList<SearchExpansionOption> Load()
    {
        try
        {
            var assembly = typeof(SearchExpansionCatalog).Assembly;
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                return [SearchExpansionOption.All];
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return [SearchExpansionOption.All];
            }

            var entries = JsonSerializer.Deserialize<List<ExpansionEntry>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (entries is null || entries.Count == 0)
            {
                return [SearchExpansionOption.All];
            }

            var options = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => new SearchExpansionOption(entry.Value, entry.Name!))
                .GroupBy(option => option.Id)
                .Select(group => group.First())
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (options.Count == 0)
            {
                return [SearchExpansionOption.All];
            }

            if (!options.Any(option => option.Id == 0))
            {
                options.Insert(0, SearchExpansionOption.All);
            }
            else
            {
                options = options
                    .OrderBy(option => option.Id == 0 ? 0 : 1)
                    .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return options;
        }
        catch (JsonException)
        {
            return [SearchExpansionOption.All];
        }
    }

    private sealed class ExpansionEntry
    {
        public string? Name { get; init; }

        public int Value { get; init; }
    }
}
