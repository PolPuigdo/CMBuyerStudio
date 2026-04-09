using CMBuyerStudio.Application.Abstractions;

namespace CMBuyerStudio.Tests.Integration.Testing;

public sealed class TestAppPaths : IAppPaths, IDisposable
{
    private readonly string _rootPath;

    public TestAppPaths()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "CMBuyerStudio.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(CardsCachePath);
        Directory.CreateDirectory(ImageCardsPath);
        Directory.CreateDirectory(ReportsPath);
        Directory.CreateDirectory(LogsPath);
    }

    public string CardsPath => Path.Combine(_rootPath, "cards.json");
    public string CachePath => Path.Combine(_rootPath, "Cache");
    public string ReportsPath => Path.Combine(_rootPath, "Reports");
    public string LogsPath => Path.Combine(_rootPath, "Logs");
    public string CardsCachePath => Path.Combine(CachePath, "CardsCache");
    public string ImageCardsPath => Path.Combine(CachePath, "CardsImages");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
