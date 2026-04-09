using System.Collections.ObjectModel;
using System.Text.Json;
using CMBuyerStudio.Domain.WantedCards;
using CMBuyerStudio.Persistence.WantedCards;
using CMBuyerStudio.Tests.Integration.Testing;

namespace CMBuyerStudio.Tests.Integration;

public sealed class JsonWantedCardsRepositoryTests
{
    [Fact]
    public async Task GetAllAsync_ReturnsEmptyWhenFileDoesNotExist()
    {
        using var paths = new TestAppPaths();
        var sut = new JsonWantedCardsRepository(paths);

        var groups = await sut.GetAllAsync();

        Assert.Empty(groups);
    }

    [Fact]
    public async Task SaveAllAsync_CreatesDirectoryAndRoundTripsGroups()
    {
        using var paths = new TestAppPaths();
        Directory.Delete(paths.CachePath, recursive: true);
        var cardsDirectory = Path.GetDirectoryName(paths.CardsPath)!;
        Directory.Delete(cardsDirectory, recursive: true);

        var sut = new JsonWantedCardsRepository(paths);
        var expected = new[]
        {
            new WantedCardGroup
            {
                CardName = "Lightning Bolt",
                DesiredQuantity = 3,
                Variants = new ObservableCollection<WantedCardVariant>
                {
                    new() { SetName = "Alpha", ProductUrl = "https://example/alpha", Price = 1.25m },
                    new() { SetName = "M11", ProductUrl = "https://example/m11", Price = 0.80m }
                }
            }
        };

        await sut.SaveAllAsync(expected);
        var savedJson = await File.ReadAllTextAsync(paths.CardsPath);
        var roundTrip = await sut.GetAllAsync();

        Assert.True(Directory.Exists(cardsDirectory));
        Assert.Contains("\"Lightning Bolt\"", savedJson);
        Assert.Single(roundTrip);
        Assert.Equal(2, roundTrip[0].Variants.Count);
        Assert.Equal(0.80m, roundTrip[0].Variants[1].Price);
    }

    [Fact]
    public async Task GetAllAsync_ThrowsWhenJsonIsMalformed()
    {
        using var paths = new TestAppPaths();
        await File.WriteAllTextAsync(paths.CardsPath, "{not-json");
        var sut = new JsonWantedCardsRepository(paths);

        await Assert.ThrowsAsync<JsonException>(() => sut.GetAllAsync());
    }
}
