using System.Net;
using System.Security.Cryptography;
using System.Text;
using CMBuyerStudio.Infrastructure.Cardmarket.Cache;
using CMBuyerStudio.Tests.Integration.Testing;

namespace CMBuyerStudio.Tests.Integration;

public sealed class CardImageCacheServiceTests
{
    [Fact]
    public async Task GetOrDownloadAsync_ReturnsEmptyForInvalidUrl()
    {
        using var paths = new TestAppPaths();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.CreateResponse(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler);
        var sut = new CardImageCacheService(httpClient, paths);

        var result = await sut.GetOrDownloadAsync("not-a-url", "card");

        Assert.Equal(string.Empty, result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetOrDownloadAsync_UsesExistingCachedFileWithoutCallingHttp()
    {
        using var paths = new TestAppPaths();
        var imageUrl = "https://images.example/card.png";
        var cachedPath = Path.Combine(paths.ImageCardsPath, $"{BuildImageStem("Lightning Bolt", imageUrl)}.png");
        await File.WriteAllBytesAsync(cachedPath, [1, 2, 3]);

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.CreateResponse(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler);
        var sut = new CardImageCacheService(httpClient, paths);

        var result = await sut.GetOrDownloadAsync(imageUrl, "Lightning Bolt");

        Assert.Equal(cachedPath, result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetOrDownloadAsync_DownloadsFileUsingContentTypeExtension()
    {
        using var paths = new TestAppPaths();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.CreateResponse(HttpStatusCode.OK, [1, 2, 3, 4], "image/png")));
        using var httpClient = new HttpClient(handler);
        var sut = new CardImageCacheService(httpClient, paths);

        var result = await sut.GetOrDownloadAsync("https://images.example/card", "Lightning Bolt");

        Assert.EndsWith(".png", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public async Task GetOrDownloadAsync_FallsBackToUrlExtensionWhenContentTypeMissing()
    {
        using var paths = new TestAppPaths();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.CreateResponse(HttpStatusCode.OK, [1, 2, 3, 4])));
        using var httpClient = new HttpClient(handler);
        var sut = new CardImageCacheService(httpClient, paths);

        var result = await sut.GetOrDownloadAsync("https://images.example/card.webp", "Lightning Bolt");

        Assert.EndsWith(".webp", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public async Task GetOrDownloadAsync_ReturnsEmptyForUnsuccessfulResponsesOrEmptyBodies()
    {
        using var paths = new TestAppPaths();
        var responses = new Queue<HttpResponseMessage>(
        [
            FakeHttpMessageHandler.CreateResponse(HttpStatusCode.BadGateway),
            FakeHttpMessageHandler.CreateResponse(HttpStatusCode.OK, [])
        ]);

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var httpClient = new HttpClient(handler);
        var sut = new CardImageCacheService(httpClient, paths);

        var failed = await sut.GetOrDownloadAsync("https://images.example/card-1.jpg", "Bolt");
        var empty = await sut.GetOrDownloadAsync("https://images.example/card-2.jpg", "Bolt");

        Assert.Equal(string.Empty, failed);
        Assert.Equal(string.Empty, empty);
    }

    private static string BuildImageStem(string imageName, string imageUrl)
    {
        var baseName = imageName.Trim().ToLowerInvariant().Replace(" ", "-");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl));
        var hash = Convert.ToHexString(bytes[..6]).ToLowerInvariant();
        return $"{baseName}_{hash}";
    }
}
