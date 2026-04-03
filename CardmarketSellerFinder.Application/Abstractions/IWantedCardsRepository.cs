using CMBuyerStudio.Domain.WantedCards;

namespace CMBuyerStudio.Application.Abstractions;

public interface IWantedCardsRepository
{
    Task<IReadOnlyList<WantedCardGroup>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAllAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default);
}