using System.Text.Json;
using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.WantedCards;

namespace CMBuyerStudio.Persistence.WantedCards;

public sealed class JsonWantedCardsRepository : IWantedCardsRepository
{
    private readonly IAppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonWantedCardsRepository(IAppPaths paths)
    {
        _paths = paths;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public async Task<IReadOnlyList<WantedCardGroup>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.CardsPath))
            return new List<WantedCardGroup>();

        await using var stream = File.OpenRead(_paths.CardsPath);

        var groups = await JsonSerializer.DeserializeAsync<List<WantedCardGroup>>(
            stream,
            _jsonOptions,
            cancellationToken);

        return groups ?? new List<WantedCardGroup>();
    }

    public async Task SaveAllAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_paths.CardsPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(_paths.CardsPath);

        await JsonSerializer.SerializeAsync(
            stream,
            groups,
            _jsonOptions,
            cancellationToken);
    }
}