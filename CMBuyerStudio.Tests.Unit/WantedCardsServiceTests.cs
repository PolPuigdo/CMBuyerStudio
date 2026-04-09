using System.Collections.ObjectModel;
using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Services;
using CMBuyerStudio.Domain.WantedCards;

namespace CMBuyerStudio.Tests.Unit;

public sealed class WantedCardsServiceTests
{
    [Fact]
    public async Task AddOrMergeAsync_MergesCaseInsensitiveQuantitiesAndVariants()
    {
        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 1, Variant("Alpha", "https://example/alpha", 1.20m))
        ]);
        var sut = new WantedCardsService(repository);

        await sut.AddOrMergeAsync(Group("lightning bolt", 2,
            Variant("Alpha", "https://example/alpha", 1.20m),
            Variant("M11", "https://example/m11", 0.80m)));

        var saved = Assert.Single(repository.SavedSnapshots);
        var group = Assert.Single(saved);
        Assert.Equal(3, group.DesiredQuantity);
        Assert.Equal(2, group.Variants.Count);
        Assert.Contains(group.Variants, variant => variant.SetName == "M11");
    }

    [Fact]
    public async Task AddOrMergeAsync_SavesClonesInsteadOfOriginalReferences()
    {
        var repository = new InMemoryWantedCardsRepository();
        var sut = new WantedCardsService(repository);
        var incoming = Group("Lightning Bolt", 1, Variant("Alpha", "https://example/alpha", 1.20m));

        await sut.AddOrMergeAsync(incoming);
        incoming.CardName = "Changed";
        incoming.Variants[0].SetName = "Changed";

        var saved = Assert.Single(repository.SavedSnapshots);
        var group = Assert.Single(saved);
        Assert.Equal("Lightning Bolt", group.CardName);
        Assert.Equal("Alpha", group.Variants[0].SetName);
        Assert.NotSame(incoming, group);
        Assert.NotSame(incoming.Variants[0], group.Variants[0]);
    }

    [Fact]
    public async Task AddOrReplaceAsync_ReplacesOnlyMatchingGroup()
    {
        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 1, Variant("Alpha", "https://example/alpha", 1.20m)),
            Group("Counterspell", 2, Variant("Ice Age", "https://example/ice-age", 0.50m))
        ]);
        var sut = new WantedCardsService(repository);

        await sut.AddOrReplaceAsync(Group("LIGHTNING BOLT", 4, Variant("M11", "https://example/m11", 0.80m)));

        var saved = Assert.Single(repository.SavedSnapshots);
        Assert.Equal(2, saved.Count);
        var replaced = Assert.Single(saved, group => group.CardName == "LIGHTNING BOLT");
        Assert.Equal(4, replaced.DesiredQuantity);
        Assert.Single(replaced.Variants);
        Assert.Equal("M11", replaced.Variants[0].SetName);
        Assert.Contains(saved, group => group.CardName == "Counterspell");
    }

    [Fact]
    public async Task AddOrReplaceAsync_AddsGroupWhenCardDoesNotExist()
    {
        var repository = new InMemoryWantedCardsRepository();
        var sut = new WantedCardsService(repository);

        await sut.AddOrReplaceAsync(Group("Counterspell", 2, Variant("Ice Age", "https://example/ice-age", 0.50m)));

        var saved = Assert.Single(repository.SavedSnapshots);
        var group = Assert.Single(saved);
        Assert.Equal("Counterspell", group.CardName);
        Assert.Equal(2, group.DesiredQuantity);
    }

    [Fact]
    public async Task Overloads_ThrowForNullGroup()
    {
        var sut = new WantedCardsService(new InMemoryWantedCardsRepository());

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.AddOrMergeAsync((WantedCardGroup?)null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.AddOrReplaceAsync((WantedCardGroup?)null));
    }

    private static WantedCardGroup Group(string cardName, int quantity, params WantedCardVariant[] variants)
    {
        return new WantedCardGroup
        {
            CardName = cardName,
            DesiredQuantity = quantity,
            Variants = new ObservableCollection<WantedCardVariant>(variants)
        };
    }

    private static WantedCardVariant Variant(string setName, string productUrl, decimal? price)
    {
        return new WantedCardVariant
        {
            SetName = setName,
            ProductUrl = productUrl,
            Price = price
        };
    }

    private sealed class InMemoryWantedCardsRepository : IWantedCardsRepository
    {
        private readonly List<WantedCardGroup> _groups;

        public InMemoryWantedCardsRepository(IEnumerable<WantedCardGroup>? groups = null)
        {
            _groups = groups?.Select(Clone).ToList() ?? [];
        }

        public List<IReadOnlyList<WantedCardGroup>> SavedSnapshots { get; } = [];

        public Task<IReadOnlyList<WantedCardGroup>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WantedCardGroup>>(_groups.Select(Clone).ToList());
        }

        public Task SaveAllAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
        {
            _groups.Clear();
            _groups.AddRange(groups.Select(Clone));
            SavedSnapshots.Add(_groups.Select(Clone).ToList());
            return Task.CompletedTask;
        }

        private static WantedCardGroup Clone(WantedCardGroup group)
        {
            return new WantedCardGroup
            {
                CardName = group.CardName,
                DesiredQuantity = group.DesiredQuantity,
                Variants = new ObservableCollection<WantedCardVariant>(group.Variants.Select(variant => new WantedCardVariant
                {
                    SetName = variant.SetName,
                    ProductUrl = variant.ProductUrl,
                    Price = variant.Price
                }))
            };
        }
    }
}
