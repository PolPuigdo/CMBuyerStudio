using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Desktop.ViewModels;
using CMBuyerStudio.Domain.Search;
using CMBuyerStudio.Domain.WantedCards;
using CMBuyerStudio.Tests.Desktop.Testing;

namespace CMBuyerStudio.Tests.Desktop;

public sealed class SearchViewModelTests
{
    [Fact]
    public async Task SearchCommand_LoadsResultsAndUpdatesSelectionState()
    {
        var wantedCardsRepository = new InMemoryWantedCardsRepository();
        var wantedCardsViewModel = new WantedCardsViewModel(wantedCardsRepository);
        var searchService = new StubCardSearchService(
        [
            SearchResult("Lightning Bolt", "M11", "https://example/m11", 0.80m),
            SearchResult("Lightning Bolt", "Alpha", "https://example/alpha", 1.20m)
        ]);
        var wantedCardsService = new RecordingWantedCardsService();
        var sut = new SearchViewModel(searchService, wantedCardsService, wantedCardsViewModel)
        {
            SearchText = "Lightning Bolt"
        };

        sut.SearchCommand.Execute(null);
        await AsyncTestHelper.WaitUntilAsync(() => !sut.IsSearching && sut.ResultsCount == 2);

        Assert.True(sut.HasResults);
        Assert.True(sut.CanSearch);
        Assert.False(sut.CanSaveSelection);

        sut.SelectAllCommand.Execute(null);

        Assert.Equal(2, sut.SelectedVariantsCount);
        Assert.True(sut.CanSaveSelection);
    }

    [Fact]
    public void SelectedQuantity_IsNormalizedToOne()
    {
        var sut = new SearchViewModel(
            new StubCardSearchService([]),
            new RecordingWantedCardsService(),
            new WantedCardsViewModel(new InMemoryWantedCardsRepository()));

        sut.SelectedQuantity = 0;

        Assert.Equal(1, sut.SelectedQuantity);
    }

    [Fact]
    public async Task SaveSelectionCommand_MapsVariantsCallsServiceReloadsWantedCardsAndClearsSelection()
    {
        var wantedCardsRepository = new InMemoryWantedCardsRepository(
        [
            new WantedCardGroup
            {
                CardName = "Counterspell",
                DesiredQuantity = 1,
                Variants =
                [
                    new WantedCardVariant { SetName = "Ice Age", ProductUrl = "https://example/ice-age", Price = 0.50m }
                ]
            }
        ]);
        var wantedCardsViewModel = new WantedCardsViewModel(wantedCardsRepository);
        await wantedCardsViewModel.InitializeAsync();

        var searchService = new StubCardSearchService(
        [
            SearchResult("Lightning Bolt", "M11", "https://example/m11", 0.80m),
            SearchResult("Lightning Bolt", "Alpha", "https://example/alpha", 1.20m)
        ]);
        var wantedCardsService = new RecordingWantedCardsService();
        var sut = new SearchViewModel(searchService, wantedCardsService, wantedCardsViewModel)
        {
            SearchText = "Lightning Bolt",
            SelectedQuantity = 3
        };

        sut.SearchCommand.Execute(null);
        await AsyncTestHelper.WaitUntilAsync(() => sut.ResultsCount == 2);
        sut.Results[0].IsSelected = true;
        sut.Results[1].IsSelected = true;

        sut.SaveSelectionCommand.Execute(null);
        await AsyncTestHelper.WaitUntilAsync(() => !sut.IsSaving && wantedCardsService.SaveCallCount == 1);

        var saved = Assert.Single(wantedCardsService.SavedGroups);
        Assert.Equal("Lightning Bolt", saved.CardName);
        Assert.Equal(3, saved.DesiredQuantity);
        Assert.Equal(2, saved.Variants.Count);
        Assert.All(sut.Results, result => Assert.False(result.IsSelected));
        Assert.Equal(1, wantedCardsViewModel.TotalGroups);
        Assert.Equal("Counterspell", wantedCardsViewModel.Groups[0].CardName);
    }

    private static SearchCardResult SearchResult(string cardName, string setName, string productUrl, decimal price)
    {
        return new SearchCardResult
        {
            CardName = cardName,
            SetName = setName,
            ProductUrl = productUrl,
            Price = price
        };
    }

    private sealed class StubCardSearchService : ICardSearchService
    {
        private readonly IReadOnlyList<SearchCardResult> _results;

        public StubCardSearchService(IReadOnlyList<SearchCardResult> results)
        {
            _results = results;
        }

        public Task<IReadOnlyList<SearchCardResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_results);
        }
    }

    private sealed class RecordingWantedCardsService : IWantedCardsService
    {
        public int SaveCallCount { get; private set; }
        public List<WantedCardGroup> SavedGroups { get; } = [];

        public Task AddOrMergeAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AddOrMergeAsync(WantedCardGroup? group, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AddOrReplaceAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            SavedGroups.AddRange(groups.Select(Clone));
            return Task.CompletedTask;
        }

        public Task AddOrReplaceAsync(WantedCardGroup? group, CancellationToken cancellationToken = default)
        {
            return AddOrReplaceAsync(group is null ? [] : [group], cancellationToken);
        }

        private static WantedCardGroup Clone(WantedCardGroup group)
        {
            return new WantedCardGroup
            {
                CardName = group.CardName,
                DesiredQuantity = group.DesiredQuantity,
                Variants = new System.Collections.ObjectModel.ObservableCollection<WantedCardVariant>(
                    group.Variants.Select(variant => new WantedCardVariant
                    {
                        SetName = variant.SetName,
                        ProductUrl = variant.ProductUrl,
                        Price = variant.Price
                    }))
            };
        }
    }

    private sealed class InMemoryWantedCardsRepository : IWantedCardsRepository
    {
        private readonly List<WantedCardGroup> _groups;

        public InMemoryWantedCardsRepository(IEnumerable<WantedCardGroup>? groups = null)
        {
            _groups = groups?.Select(Clone).ToList() ?? [];
        }

        public Task<IReadOnlyList<WantedCardGroup>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WantedCardGroup>>(_groups.Select(Clone).ToList());
        }

        public Task SaveAllAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
        {
            _groups.Clear();
            _groups.AddRange(groups.Select(Clone));
            return Task.CompletedTask;
        }

        private static WantedCardGroup Clone(WantedCardGroup group)
        {
            return new WantedCardGroup
            {
                CardName = group.CardName,
                DesiredQuantity = group.DesiredQuantity,
                Variants = new System.Collections.ObjectModel.ObservableCollection<WantedCardVariant>(
                    group.Variants.Select(variant => new WantedCardVariant
                    {
                        SetName = variant.SetName,
                        ProductUrl = variant.ProductUrl,
                        Price = variant.Price
                    }))
            };
        }
    }
}
