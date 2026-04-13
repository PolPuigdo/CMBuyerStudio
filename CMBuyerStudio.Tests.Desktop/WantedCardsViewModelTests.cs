using System.Collections.ObjectModel;
using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Desktop.ViewModels;
using CMBuyerStudio.Domain.WantedCards;
using CMBuyerStudio.Tests.Desktop.Testing;

namespace CMBuyerStudio.Tests.Desktop;

public sealed class WantedCardsViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsGroupsWithoutSaving()
    {
        var feedbackService = new FakeUserFeedbackService();
        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 2, Variant("Alpha", "https://example/alpha", 1.20m))
        ]);
        var sut = new WantedCardsViewModel(repository, feedbackService);

        await sut.InitializeAsync();

        Assert.Single(sut.Groups);
        Assert.Equal(1, sut.TotalGroups);
        Assert.Equal(0, repository.SaveCallCount);
    }

    [Fact]
    public async Task ChangingDesiredQuantity_TriggersSave()
    {
        var feedbackService = new FakeUserFeedbackService();
        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 2, Variant("Alpha", "https://example/alpha", 1.20m))
        ]);
        var sut = new WantedCardsViewModel(repository, feedbackService);
        await sut.InitializeAsync();

        sut.Groups[0].DesiredQuantity = 5;
        await AsyncTestHelper.WaitUntilAsync(() => repository.SaveCallCount >= 1);

        Assert.Equal(5, repository.LastSavedSnapshot!.Single().DesiredQuantity);
    }

    [Fact]
    public async Task RemoveVariantCommand_WhenConfirmationRejected_DoesNotMutateOrPersist()
    {
        var feedbackService = new FakeUserFeedbackService();
        feedbackService.EnqueueConfirmResult(false);

        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 2, Variant("Alpha", "https://example/alpha", 1.20m))
        ]);
        var sut = new WantedCardsViewModel(repository, feedbackService);
        await sut.InitializeAsync();
        var variant = sut.Groups[0].Variants[0];

        sut.RemoveVariantCommand.Execute(variant);

        await Task.Delay(120);
        Assert.Single(sut.Groups);
        Assert.Single(sut.Groups[0].Variants);
        Assert.Equal(0, repository.SaveCallCount);
        Assert.Empty(feedbackService.Notifications);
    }

    [Fact]
    public async Task RemoveVariantCommand_WhenConfirmed_RemovesEmptyGroupPersistsAndNotifies()
    {
        var feedbackService = new FakeUserFeedbackService();
        feedbackService.EnqueueConfirmResult(true);

        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 2, Variant("Alpha", "https://example/alpha", 1.20m))
        ]);
        var sut = new WantedCardsViewModel(repository, feedbackService);
        await sut.InitializeAsync();
        var variant = sut.Groups[0].Variants[0];

        sut.RemoveVariantCommand.Execute(variant);
        await AsyncTestHelper.WaitUntilAsync(() => repository.SaveCallCount >= 1);

        Assert.Empty(sut.Groups);
        Assert.Equal(0, sut.TotalGroups);
        Assert.Empty(repository.LastSavedSnapshot!);
        Assert.Single(feedbackService.Notifications);
        Assert.Equal("Variant \"Alpha\" deleted successfully.", feedbackService.Notifications[0].Message);
    }

    [Fact]
    public async Task DeleteGroupCommand_WhenConfirmationRejected_DoesNotMutateOrPersist()
    {
        var feedbackService = new FakeUserFeedbackService();
        feedbackService.EnqueueConfirmResult(false);

        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 2, Variant("Alpha", "https://example/alpha", 1.20m)),
            Group("Counterspell", 1, Variant("Ice Age", "https://example/ice-age", 0.50m))
        ]);
        var sut = new WantedCardsViewModel(repository, feedbackService);
        await sut.InitializeAsync();

        sut.DeleteGroupCommand.Execute(sut.Groups[0]);

        await Task.Delay(120);
        Assert.Equal(2, sut.TotalGroups);
        Assert.Equal(0, repository.SaveCallCount);
        Assert.Empty(feedbackService.Notifications);
    }

    [Fact]
    public async Task DeleteGroupAndClearAll_WhenConfirmed_KeepTotalGroupsInSyncAndNotify()
    {
        var feedbackService = new FakeUserFeedbackService();
        feedbackService.EnqueueConfirmResult(true);
        feedbackService.EnqueueConfirmResult(true);

        var repository = new InMemoryWantedCardsRepository(
        [
            Group("Lightning Bolt", 2, Variant("Alpha", "https://example/alpha", 1.20m)),
            Group("Counterspell", 1, Variant("Ice Age", "https://example/ice-age", 0.50m))
        ]);
        var sut = new WantedCardsViewModel(repository, feedbackService);
        await sut.InitializeAsync();

        sut.DeleteGroupCommand.Execute(sut.Groups[0]);
        await AsyncTestHelper.WaitUntilAsync(() => repository.SaveCallCount >= 1);
        Assert.Single(sut.Groups);
        Assert.Equal(1, sut.TotalGroups);

        sut.ClearAllCommand.Execute(null);
        await AsyncTestHelper.WaitUntilAsync(() => repository.SaveCallCount >= 2);
        Assert.Empty(sut.Groups);
        Assert.Equal(0, sut.TotalGroups);
        Assert.Equal(2, feedbackService.Notifications.Count);
        Assert.Equal("Card \"Lightning Bolt\" deleted successfully.", feedbackService.Notifications[0].Message);
        Assert.Equal("All wanted cards deleted successfully.", feedbackService.Notifications[1].Message);
    }

    [Fact]
    public async Task ClearAllCommand_WhenNoGroups_DoesNothing()
    {
        var feedbackService = new FakeUserFeedbackService();
        var repository = new InMemoryWantedCardsRepository([]);
        var sut = new WantedCardsViewModel(repository, feedbackService);
        await sut.InitializeAsync();

        sut.ClearAllCommand.Execute(null);

        await Task.Delay(120);
        Assert.Empty(feedbackService.ConfirmCalls);
        Assert.Empty(feedbackService.Notifications);
        Assert.Equal(0, repository.SaveCallCount);
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

        public InMemoryWantedCardsRepository(IEnumerable<WantedCardGroup> groups)
        {
            _groups = groups.Select(Clone).ToList();
        }

        public int SaveCallCount { get; private set; }
        public IReadOnlyList<WantedCardGroup>? LastSavedSnapshot { get; private set; }

        public Task<IReadOnlyList<WantedCardGroup>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WantedCardGroup>>(_groups.Select(Clone).ToList());
        }

        public Task SaveAllAsync(IEnumerable<WantedCardGroup> groups, CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            _groups.Clear();
            _groups.AddRange(groups.Select(Clone));
            LastSavedSnapshot = _groups.Select(Clone).ToList();
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
