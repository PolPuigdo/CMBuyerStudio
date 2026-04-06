using CMBuyerStudio.Domain.WantedCards;

namespace CMBuyerStudio.Application.Abstractions;

public interface IWantedCardsService
{
    Task AddOrMergeAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default);
    Task AddOrMergeAsync(WantedCardGroup? group, CancellationToken cancellationToken = default);
    Task AddOrReplaceAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default);
    Task AddOrReplaceAsync(WantedCardGroup? group, CancellationToken cancellationToken = default);
}