using CMBuyerStudio.Application.Common.Options;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Application.Optimization;
using CMBuyerStudio.Domain.Market;
using Microsoft.Extensions.Options;

namespace CMBuyerStudio.Tests.Unit;

public sealed class PurchaseOptimizerTests
{
    [Fact]
    public void Optimize_ReturnsPreselectedSellersWhenRemainingRequiredIsZero()
    {
        var sut = CreateSut();
        var card = Card(
            "p1",
            desiredQuantity: 1,
            Offer("Preselected", "p1", price: 1m, availableQuantity: 1));

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [card],
            purgedMarketData: [card],
            remainingRequiredByCardKey: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["p1"] = 0
            },
            preselectedSellerNames: ["Preselected"]));

        Assert.Equal(["Preselected"], result.SelectedSellerNames);
        Assert.Equal(1, result.SellerCount);
        Assert.Equal(1m, result.CardsTotalPrice);
        Assert.Empty(result.UncoveredCardKeys);

        var assignment = Assert.Single(result.Assignments);
        Assert.Equal("Preselected", assignment.SellerName);
        Assert.Equal(1, assignment.Quantity);
    }

    [Fact]
    public void Optimize_LeavesImpossibleCardsUncoveredWithoutBlockingCoveredCards()
    {
        var sut = CreateSut();
        var impossible = Card("p1", desiredQuantity: 1);
        var covered = Card(
            "p2",
            desiredQuantity: 1,
            Offer("GoodSeller", "p2", price: 2m, availableQuantity: 1));

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [impossible, covered],
            purgedMarketData: [impossible, covered],
            remainingRequiredByCardKey: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["p1"] = 0,
                ["p2"] = 1
            },
            uncoveredCardKeys: ["p1"]));

        Assert.Equal(["GoodSeller"], result.SelectedSellerNames);
        Assert.Equal(["p1"], result.UncoveredCardKeys.OrderBy(x => x));
        var assignment = Assert.Single(result.Assignments);
        Assert.Equal("p2", assignment.ProductUrl);
    }

    [Fact]
    public void Optimize_PrefersLowerFixedCostWhenSellerCountMatches()
    {
        var sut = CreateSut();
        var cardA = Card(
            "p1",
            desiredQuantity: 1,
            Offer("CheapShip", "p1", price: 1m, availableQuantity: 1),
            Offer("ExpensiveShip", "p1", price: 1m, availableQuantity: 1));
        var cardB = Card(
            "p2",
            desiredQuantity: 1,
            Offer("CheapShip", "p2", price: 1m, availableQuantity: 1),
            Offer("ExpensiveShip", "p2", price: 1m, availableQuantity: 1));

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [cardA, cardB],
            purgedMarketData: [cardA, cardB],
            remainingRequiredByCardKey: Remaining("p1", 1, "p2", 1),
            fixedCostBySellerName: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["CheapShip"] = 1m,
                ["ExpensiveShip"] = 5m
            }));

        Assert.Equal(["CheapShip"], result.SelectedSellerNames);
        Assert.Equal(2m, result.CardsTotalPrice);
        Assert.Empty(result.UncoveredCardKeys);
    }

    [Fact]
    public void Optimize_SplitsQuantitiesAcrossMultipleSellersAndBuildsAssignments()
    {
        var sut = CreateSut();
        var card = Card(
            "p1",
            desiredQuantity: 3,
            Offer("SellerA", "p1", price: 1m, availableQuantity: 2),
            Offer("SellerB", "p1", price: 1.2m, availableQuantity: 1),
            Offer("SellerC", "p1", price: 5m, availableQuantity: 3));

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [card],
            purgedMarketData: [card],
            remainingRequiredByCardKey: Remaining("p1", 3)));

        Assert.Equal(["SellerA", "SellerB"], result.SelectedSellerNames);
        Assert.Equal(3.2m, result.CardsTotalPrice);
        Assert.Empty(result.UncoveredCardKeys);

        Assert.Equal(2, result.Assignments.Count);
        Assert.Contains(result.Assignments, x => x.SellerName == "SellerA" && x.Quantity == 2);
        Assert.Contains(result.Assignments, x => x.SellerName == "SellerB" && x.Quantity == 1);
    }

    [Fact]
    public void Optimize_BreaksExactTiesLexicographically()
    {
        var sut = CreateSut();
        var card = Card(
            "p1",
            desiredQuantity: 1,
            Offer("SellerA", "p1", price: 1m, availableQuantity: 1),
            Offer("SellerB", "p1", price: 1m, availableQuantity: 1));

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [card],
            purgedMarketData: [card],
            remainingRequiredByCardKey: Remaining("p1", 1),
            fixedCostBySellerName: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["SellerA"] = 1m,
                ["SellerB"] = 1m
            }));

        Assert.Equal(["SellerA"], result.SelectedSellerNames);
    }

    [Fact]
    public void Optimize_UsesPreselectedAndSolvedSellersWhenBuildingAssignments()
    {
        var sut = CreateSut();
        var firstCard = Card(
            "p1",
            desiredQuantity: 1,
            Offer("ForcedSeller", "p1", price: 1m, availableQuantity: 1));
        var secondCardScoped = Card(
            "p2",
            desiredQuantity: 1,
            Offer("SolvedSeller", "p2", price: 2m, availableQuantity: 1));
        var secondCardPurged = Card(
            "p2",
            desiredQuantity: 1,
            Offer("SolvedSeller", "p2", price: 2m, availableQuantity: 1));

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [firstCard, secondCardScoped],
            purgedMarketData: [Card("p1", desiredQuantity: 1), secondCardPurged],
            remainingRequiredByCardKey: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["p1"] = 0,
                ["p2"] = 1
            },
            preselectedSellerNames: ["ForcedSeller"]));

        Assert.Equal(
            ["ForcedSeller", "SolvedSeller"],
            result.SelectedSellerNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(result.UncoveredCardKeys);
        Assert.Contains(result.Assignments, x => x.SellerName == "ForcedSeller" && x.ProductUrl == "p1");
        Assert.Contains(result.Assignments, x => x.SellerName == "SolvedSeller" && x.ProductUrl == "p2");
    }

    [Fact]
    public void Optimize_PartialSelection_PrefersMoreFullyCoveredCardsOverCheaperLowerCoverage()
    {
        var sut = CreateSut();
        var mainCard = Card(
            "p1",
            desiredQuantity: 2,
            Offer("WideCoverageA", "p1", price: 1m, availableQuantity: 2),
            Offer("CheapButShallow", "p1", price: 0.1m, availableQuantity: 1));
        var secondCard = Card(
            "p2",
            desiredQuantity: 1,
            Offer("WideCoverageB", "p2", price: 1m, availableQuantity: 1));
        var impossibleCard = Card("p3", desiredQuantity: 1);

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [mainCard, secondCard, impossibleCard],
            purgedMarketData: [mainCard, secondCard, impossibleCard],
            remainingRequiredByCardKey: Remaining("p1", 2, "p2", 1, "p3", 1)));

        Assert.Contains("WideCoverageA", result.SelectedSellerNames);
        Assert.Contains("WideCoverageB", result.SelectedSellerNames);
        Assert.Equal(["p3"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2.1m, result.CardsTotalPrice);
    }

    [Fact]
    public void Optimize_PartialSelection_PrefersMoreCoveredUnitsWhenFullyCoveredCardsTie()
    {
        var sut = CreateSut();
        var partialCard = Card(
            "p1",
            desiredQuantity: 3,
            Offer("BulkSeller", "p1", price: 1m, availableQuantity: 2),
            Offer("LastUnitSeller", "p1", price: 10m, availableQuantity: 1));
        var impossibleCard = Card("p2", desiredQuantity: 1);

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [partialCard, impossibleCard],
            purgedMarketData: [partialCard, impossibleCard],
            remainingRequiredByCardKey: Remaining("p1", 3, "p2", 1)));

        Assert.Equal(
            ["BulkSeller", "LastUnitSeller"],
            result.SelectedSellerNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["p2"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(12m, result.CardsTotalPrice);
    }

    [Fact]
    public void Optimize_PartialSelection_PrefersLowerCoveredCostWhenCoverageTies()
    {
        var sut = CreateSut();
        var coveredCard = Card(
            "p1",
            desiredQuantity: 1,
            Offer("CheaperSeller", "p1", price: 1m, availableQuantity: 1),
            Offer("ExpensiveSeller", "p1", price: 3m, availableQuantity: 1));
        var impossibleCard = Card("p2", desiredQuantity: 1);

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [coveredCard, impossibleCard],
            purgedMarketData: [coveredCard, impossibleCard],
            remainingRequiredByCardKey: Remaining("p1", 1, "p2", 1)));

        Assert.Equal(["CheaperSeller"], result.SelectedSellerNames);
        Assert.Equal(["p2"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1m, result.CardsTotalPrice);
    }

    [Fact]
    public void Optimize_UsesRequestKeyForUncoveredCards()
    {
        var sut = CreateSut();
        var card = Card(
            productUrl: "https://example.com/lightning-bolt-set-a",
            desiredQuantity: 2,
            requestKey: "Lightning Bolt");

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [card],
            purgedMarketData: [card],
            remainingRequiredByCardKey: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Lightning Bolt"] = 2
            }));

        Assert.Equal(["Lightning Bolt"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(result.Assignments);
    }

    [Fact]
    public void Optimize_PartialSelection_BreaksExactTiesLexicographically()
    {
        var sut = CreateSut();
        var coveredCard = Card(
            "p1",
            desiredQuantity: 1,
            Offer("SellerA", "p1", price: 1m, availableQuantity: 1),
            Offer("SellerB", "p1", price: 1m, availableQuantity: 1));
        var impossibleCard = Card("p2", desiredQuantity: 1);

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [coveredCard, impossibleCard],
            purgedMarketData: [coveredCard, impossibleCard],
            remainingRequiredByCardKey: Remaining("p1", 1, "p2", 1),
            fixedCostBySellerName: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["SellerA"] = 1m,
                ["SellerB"] = 1m
            }));

        Assert.Equal(["SellerA"], result.SelectedSellerNames);
        Assert.Equal(["p2"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Optimize_PartialSelection_KeepsFullCoveragePathUnchanged()
    {
        var sut = CreateSut();
        var firstCard = Card(
            "p1",
            desiredQuantity: 1,
            Offer("SellerA", "p1", price: 1m, availableQuantity: 1),
            Offer("SellerB", "p1", price: 2m, availableQuantity: 1));
        var secondCard = Card(
            "p2",
            desiredQuantity: 1,
            Offer("SellerA", "p2", price: 1m, availableQuantity: 1),
            Offer("SellerB", "p2", price: 2m, availableQuantity: 1));

        var result = sut.Optimize(Snapshot(
            scopedMarketData: [firstCard, secondCard],
            purgedMarketData: [firstCard, secondCard],
            remainingRequiredByCardKey: Remaining("p1", 1, "p2", 1)));

        Assert.Equal(["SellerA"], result.SelectedSellerNames);
        Assert.Empty(result.UncoveredCardKeys);
        Assert.Equal(2m, result.CardsTotalPrice);
    }

    [Fact]
    public void Optimize_PartialRegression_MatchesRandom31Fixture()
    {
        var sut = CreateSut();
        var scenario = BuildRandomScenario(31);

        var result = sut.Optimize(Snapshot(
            scopedMarketData: scenario.Cards,
            purgedMarketData: scenario.Cards,
            remainingRequiredByCardKey: RemainingForScenario(scenario),
            fixedCostBySellerName: scenario.FixedCosts));

        Assert.Equal(["S2"], result.SelectedSellerNames);
        Assert.Equal(["rnd-30-p2"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2.10m, result.CardsTotalPrice);
    }

    [Fact]
    public void Optimize_PartialRegression_MatchesRandom40Fixture()
    {
        var sut = CreateSut();
        var scenario = BuildRandomScenario(40);

        var result = sut.Optimize(Snapshot(
            scopedMarketData: scenario.Cards,
            purgedMarketData: scenario.Cards,
            remainingRequiredByCardKey: RemainingForScenario(scenario),
            fixedCostBySellerName: scenario.FixedCosts));

        Assert.Equal(
            ["S2", "S3"],
            result.SelectedSellerNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["rnd-39-p1"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(5.28m, result.CardsTotalPrice);
    }

    [Fact]
    public void Optimize_PartialRegression_MatchesRandom45Fixture()
    {
        var sut = CreateSut();
        var scenario = BuildRandomScenario(45);

        var result = sut.Optimize(Snapshot(
            scopedMarketData: scenario.Cards,
            purgedMarketData: scenario.Cards,
            remainingRequiredByCardKey: RemainingForScenario(scenario),
            fixedCostBySellerName: scenario.FixedCosts));

        Assert.Equal(
            ["S4", "S5"],
            result.SelectedSellerNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["rnd-44-p1"], result.UncoveredCardKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(7.06m, result.CardsTotalPrice);
    }

    [Fact]
    public void Optimize_SolvesLargerFixtureWithinReasonableTime()
    {
        var sut = CreateSut(new PurchaseOptimizerOptions
        {
            CandidatePoolMin = 8,
            CandidatePoolMax = 12,
            ExactMaxK = 6,
            BeamWidth = 250
        });

        var cards = new List<MarketCardData>();
        var groupedSellers = new[]
        {
            ("GroupA", new[] { "p1", "p2", "p3" }),
            ("GroupB", new[] { "p4", "p5", "p6" }),
            ("GroupC", new[] { "p7", "p8", "p9" }),
            ("GroupD", new[] { "p10", "p11", "p12" })
        };

        for (var index = 1; index <= 12; index++)
        {
            var productUrl = $"p{index}";
            var offers = new List<SellerOffer>
            {
                Offer("FullSeller", productUrl, price: 2.2m, availableQuantity: 1)
            };

            foreach (var (sellerName, productUrls) in groupedSellers)
            {
                if (productUrls.Contains(productUrl, StringComparer.OrdinalIgnoreCase))
                {
                    offers.Add(Offer(sellerName, productUrl, price: 1m, availableQuantity: 1));
                }
            }

            for (var distractor = 1; distractor <= 2; distractor++)
            {
                offers.Add(Offer($"Distractor_{productUrl}_{distractor}", productUrl, price: 3m + (distractor / 10m), availableQuantity: 1));
            }

            cards.Add(Card(productUrl, desiredQuantity: 1, [.. offers]));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = sut.Optimize(Snapshot(
            scopedMarketData: cards,
            purgedMarketData: cards,
            remainingRequiredByCardKey: cards.ToDictionary(
                card => card.Target.ProductUrl,
                _ => 1,
                StringComparer.OrdinalIgnoreCase),
            fixedCostBySellerName: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["FullSeller"] = 3m,
                ["GroupA"] = 0.4m,
                ["GroupB"] = 0.4m,
                ["GroupC"] = 0.4m,
                ["GroupD"] = 0.4m
            }));
        stopwatch.Stop();

        Assert.Equal(
            ["GroupA", "GroupB", "GroupC", "GroupD"],
            result.SelectedSellerNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(result.UncoveredCardKeys);
        Assert.Equal(12, result.Assignments.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    private static PurchaseOptimizer CreateSut(PurchaseOptimizerOptions? options = null)
        => new(Options.Create(options ?? new PurchaseOptimizerOptions()));

    private static PurgedScopeSnapshot Snapshot(
        IReadOnlyList<MarketCardData> scopedMarketData,
        IReadOnlyList<MarketCardData> purgedMarketData,
        IReadOnlyDictionary<string, int>? remainingRequiredByCardKey = null,
        IReadOnlyDictionary<string, decimal>? fixedCostBySellerName = null,
        IEnumerable<string>? preselectedSellerNames = null,
        IEnumerable<string>? uncoveredCardKeys = null)
    {
        return new PurgedScopeSnapshot
        {
            ScopedMarketData = scopedMarketData,
            PurgedMarketData = purgedMarketData,
            RemainingRequiredByCardKey = remainingRequiredByCardKey
                ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            FixedCostBySellerName = fixedCostBySellerName
                ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
            PreselectedSellerNames = preselectedSellerNames?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            UncoveredCardKeys = uncoveredCardKeys?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static Dictionary<string, int> Remaining(params object[] values)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < values.Length; index += 2)
        {
            result[(string)values[index]] = (int)values[index + 1];
        }

        return result;
    }

    private static MarketCardData Card(string productUrl, int desiredQuantity, params SellerOffer[] offers)
        => Card(productUrl, desiredQuantity, requestKey: string.Empty, offers);

    private static MarketCardData Card(string productUrl, int desiredQuantity, string requestKey, params SellerOffer[] offers)
    {
        return new MarketCardData
        {
            Target = new ScrapingTarget
            {
                RequestKey = requestKey,
                CardName = productUrl,
                SetName = "SET",
                ProductUrl = productUrl,
                DesiredQuantity = desiredQuantity
            },
            ScrapedAtUtc = DateTime.UtcNow,
            Offers = offers
        };
    }

    private static SellerOffer Offer(
        string sellerName,
        string productUrl,
        decimal price,
        int availableQuantity)
    {
        return new SellerOffer
        {
            SellerName = sellerName,
            Country = "ES",
            Price = price,
            AvailableQuantity = availableQuantity,
            CardName = productUrl,
            SetName = "SET",
            ProductUrl = productUrl
        };
    }

    private static Dictionary<string, int> RemainingForScenario(TestScenario scenario)
        => scenario.Cards.ToDictionary(
            card => card.Target.ProductUrl,
            card => card.Target.DesiredQuantity,
            StringComparer.OrdinalIgnoreCase);

    private static TestScenario BuildRandomScenario(int scenarioNumber, int seed = 12345)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scenarioNumber);

        var random = new Random(seed);

        for (var scenarioIndex = 0; scenarioIndex < scenarioNumber; scenarioIndex++)
        {
            var cardCount = random.Next(2, 7);
            var sellerCount = random.Next(2, 9);
            var sellerNames = Enumerable.Range(1, sellerCount).Select(index => $"S{index}").ToArray();
            var fixedCosts = sellerNames.ToDictionary(
                name => name,
                _ => decimal.Round((decimal)random.NextDouble() * 3m, 2),
                StringComparer.OrdinalIgnoreCase);

            var cards = new List<MarketCardData>();
            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                var productUrl = $"rnd-{scenarioIndex}-p{cardIndex + 1}";
                var desired = random.Next(1, 4);
                var offers = new List<SellerOffer>();

                foreach (var sellerName in sellerNames)
                {
                    if (random.NextDouble() < 0.45d)
                    {
                        offers.Add(Offer(
                            sellerName,
                            productUrl,
                            decimal.Round(0.4m + ((decimal)random.NextDouble() * 5m), 2),
                            random.Next(1, 4)));
                    }
                }

                if (offers.Count == 0)
                {
                    offers.Add(Offer(
                        sellerNames[random.Next(sellerNames.Length)],
                        productUrl,
                        decimal.Round(0.4m + ((decimal)random.NextDouble() * 5m), 2),
                        random.Next(1, 4)));
                }

                cards.Add(Card(productUrl, desired, [.. offers]));
            }

            if (scenarioIndex == scenarioNumber - 1)
            {
                return new TestScenario(cards, fixedCosts);
            }
        }

        throw new InvalidOperationException($"No se pudo generar el escenario random-{scenarioNumber}.");
    }

    private sealed record TestScenario(
        IReadOnlyList<MarketCardData> Cards,
        IReadOnlyDictionary<string, decimal> FixedCosts);
}
