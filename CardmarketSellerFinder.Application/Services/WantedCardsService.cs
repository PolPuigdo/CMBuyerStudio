using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.WantedCards;

namespace CMBuyerStudio.Application.Services;

public sealed class WantedCardsService : IWantedCardsService
{
    private readonly IWantedCardsRepository _wantedCardsRepository;

    public WantedCardsService(IWantedCardsRepository wantedCardsRepository)
    {
        _wantedCardsRepository = wantedCardsRepository;
    }

    public async Task AddOrMergeAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
    {
        var existingGroups = (await _wantedCardsRepository.GetAllAsync(cancellationToken)).ToList();

        foreach (var incomingGroup in groups)
        {
            var existingGroup = existingGroups.FirstOrDefault(g =>
                string.Equals(g.CardName, incomingGroup.CardName, StringComparison.OrdinalIgnoreCase));

            if (existingGroup is null)
            {
                existingGroups.Add(CloneGroup(incomingGroup));
                continue;
            }

            existingGroup.DesiredQuantity += incomingGroup.DesiredQuantity;

            foreach (var incomingVariant in incomingGroup.Variants)
            {
                var variantExists = existingGroup.Variants.Any(v =>
                    string.Equals(v.SetName, incomingVariant.SetName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(v.ProductUrl, incomingVariant.ProductUrl, StringComparison.OrdinalIgnoreCase));

                if (!variantExists)
                {
                    existingGroup.Variants.Add(new WantedCardVariant
                    {
                        SetName = incomingVariant.SetName,
                        ProductUrl = incomingVariant.ProductUrl,
                        Price = incomingVariant.Price,
                        ImagePath = incomingVariant.ImagePath
                    });
                }
            }
        }

        await _wantedCardsRepository.SaveAllAsync(existingGroups, cancellationToken);
    }

    public async Task AddOrMergeAsync(WantedCardGroup? group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        await AddOrMergeAsync(new List<WantedCardGroup> { group }, cancellationToken);
    }

    public async Task AddOrReplaceAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
    {
        var existingGroups = (await _wantedCardsRepository.GetAllAsync(cancellationToken)).ToList();

        foreach (var incomingGroup in groups)
        {
            var existingIndex = existingGroups.FindIndex(g =>
                string.Equals(g.CardName, incomingGroup.CardName, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                existingGroups.Add(CloneGroup(incomingGroup));
                continue;
            }

            existingGroups[existingIndex] = CloneGroup(incomingGroup);
        }

        await _wantedCardsRepository.SaveAllAsync(existingGroups, cancellationToken);
    }

    public async Task AddOrReplaceAsync(WantedCardGroup? group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        await AddOrReplaceAsync(new[] { group }, cancellationToken);
    }

    private static WantedCardGroup CloneGroup(WantedCardGroup source)
    {
        return new WantedCardGroup
        {
            CardName = source.CardName,
            DesiredQuantity = source.DesiredQuantity,
            Variants = new System.Collections.ObjectModel.ObservableCollection<WantedCardVariant>(
                source.Variants.Select(v => new WantedCardVariant
                {
                    SetName = v.SetName,
                    ProductUrl = v.ProductUrl,
                    Price = v.Price,
                    ImagePath = v.ImagePath
                }))
        };
    }
}
