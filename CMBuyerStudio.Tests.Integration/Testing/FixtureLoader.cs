namespace CMBuyerStudio.Tests.Integration.Testing;

public static class FixtureLoader
{
    public static string LoadText(string relativePath)
    {
        var fullPath = Path.Combine(GetRepositoryRoot(), "CMBuyerStudio.Tests.Integration", "Fixtures", relativePath);
        return File.ReadAllText(fullPath);
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CMBuyerStudio.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test execution directory.");
    }
}
