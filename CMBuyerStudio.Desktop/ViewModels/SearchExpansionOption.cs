namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class SearchExpansionOption
{
    public static SearchExpansionOption All { get; } = new(0, "All");

    public SearchExpansionOption(int id, string name)
    {
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? $"Expansion {id}" : name.Trim();
    }

    public int Id { get; }

    public string Name { get; }

    public override string ToString() => Name;
}
