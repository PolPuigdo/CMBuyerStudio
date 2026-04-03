using CMBuyerStudio.Application.Abstractions;

namespace CMBuyerStudio.Infrastructure.Paths;

public sealed class AppPaths : IAppPaths
{
    private readonly string _basePath;

    public AppPaths()
    {
        _basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CMBuyerStudio");

        EnsureDirectories();
    }

    public string CardsPath => Path.Combine(_basePath, "cards.json");
    public string CachePath => Path.Combine(_basePath, "Cache");
    public string ReportsPath => Path.Combine(_basePath, "Reports");
    public string LogsPath => Path.Combine(_basePath, "Logs");

    private void EnsureDirectories()
    {
        // Base directory
        Directory.CreateDirectory(_basePath);

        // Subdirectories
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(ReportsPath);
        Directory.CreateDirectory(LogsPath);
    }
}