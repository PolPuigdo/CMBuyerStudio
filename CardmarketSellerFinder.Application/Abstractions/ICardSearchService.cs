using CMBuyerStudio.Domain.Search;

namespace CMBuyerStudio.Application.Abstractions
{
    public interface ICardSearchService
    {
        Task<IReadOnlyList<SearchCardVariantResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
    }
}
