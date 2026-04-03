using System.Text.Json;
using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.Entities;

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

    public async Task<IReadOnlyList<CardWanted>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.CardsPath))
            return new List<CardWanted>();

        await using var stream = File.OpenRead(_paths.CardsPath);

        var cards = await JsonSerializer.DeserializeAsync<List<CardWanted>>(stream, _jsonOptions, cancellationToken);

        return cards ?? new List<CardWanted>();
    }

    public async Task AddAsync(CardWanted card, CancellationToken cancellationToken)
    {
        var cards = (await GetAllAsync(cancellationToken)).ToList();

        cards.Add(card);

        await SaveAsync(cards, cancellationToken);
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var cards = (await GetAllAsync(cancellationToken)).ToList();

        cards.RemoveAll(c => c.Id == id);

        await SaveAsync(cards, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(new List<CardWanted>(), cancellationToken);
    }

    private async Task SaveAsync(List<CardWanted> cards, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_paths.CardsPath);

        await JsonSerializer.SerializeAsync(stream, cards, _jsonOptions, cancellationToken);
    }
}