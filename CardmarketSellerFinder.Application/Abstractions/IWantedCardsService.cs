using CMBuyerStudio.Domain.WantedCards;

namespace CMBuyerStudio.Application.Abstractions;

public interface IWantedCardsService
{
    Task AddOrMergeAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default);
}