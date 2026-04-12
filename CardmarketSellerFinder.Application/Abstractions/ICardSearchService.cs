using CMBuyerStudio.Domain.Search;

namespace CMBuyerStudio.Application.Abstractions
{
    public interface ICardSearchService
    {
        Task<IReadOnlyList<SearchCardResult>> SearchAsync(
            string query,
            int expansionId = 0,
            CancellationToken cancellationToken = default);
    }
}
