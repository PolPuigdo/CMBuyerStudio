using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.Search;
using System.Collections.ObjectModel;

namespace CMBuyerStudio.Persistence.Search;

public sealed class FakeCardSearchService : ICardSearchService
{
    public Task<IReadOnlyList<SearchCardResult>> SearchAsync(
    string query,
    CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return Task.FromResult<IReadOnlyList<SearchCardResult>>(new List<SearchCardResult>());

        var results = new List<SearchCardResult>
    {
        new()
        {
            CardName = normalizedQuery,
            SetName = "Magic 2010",
            ProductUrl = $"https://www.cardmarket.com/es/Magic/Products/Singles/Magic-2010/{normalizedQuery.Replace(' ', '-')}",
            Price = 1.20m
        },
        new()
        {
            CardName = normalizedQuery,
            SetName = "Double Masters",
            ProductUrl = $"https://www.cardmarket.com/es/Magic/Products/Singles/Double-Masters/{normalizedQuery.Replace(' ', '-')}",
            Price = 2.10m
        },
        new()
        {
            CardName = normalizedQuery,
            SetName = "Secret Lair",
            ProductUrl = $"https://www.cardmarket.com/es/Magic/Products/Singles/Secret-Lair/{normalizedQuery.Replace(' ', '-')}",
            Price = 5.50m
        }
    };

        return Task.FromResult<IReadOnlyList<SearchCardResult>>(results);
    }
}