namespace CMBuyerStudio.Application.Abstractions;

using CMBuyerStudio.Domain.Entities;

public interface IWantedCardsRepository
{
    Task<IReadOnlyList<CardWanted>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(CardWanted card, CancellationToken cancellationToken);

    Task RemoveAsync(Guid id, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}