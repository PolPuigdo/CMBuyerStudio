using CMBuyerStudio.Domain.Search;

namespace CMBuyerStudio.Application.Abstractions
{
    public interface ICardSearchService
    {
        Task<IReadOnlyList<SearchCardResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
    }
}
