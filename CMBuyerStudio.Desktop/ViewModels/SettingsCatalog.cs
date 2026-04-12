namespace CMBuyerStudio.Desktop.ViewModels;

public static class SettingsCatalog
{
    public static IReadOnlyList<string> ShippingCountries { get; } =
    [
        "Austria",
        "Belgium",
        "Bulgaria",
        "Canada",
        "Croatia",
        "Cyprus",
        "Czech Republic",
        "Denmark",
        "Estonia",
        "Finland",
        "France",
        "Germany",
        "Greece",
        "Hungary",
        "Iceland",
        "Ireland",
        "Italy",
        "Japan",
        "Latvia",
        "Liechtenstein",
        "Lithuania",
        "Luxembourg",
        "Malta",
        "Netherlands",
        "Norway",
        "Poland",
        "Portugal",
        "Romania",
        "Singapore",
        "Slovakia",
        "Slovenia",
        "Spain",
        "Sweden",
        "Switzerland"
    ];

    public static IReadOnlyList<SettingsOptionCatalogItem> SellerCountries { get; } =
    [
        new(1, "Austria"),
        new(2, "Belgium"),
        new(3, "Bulgaria"),
        new(33, "Canada"),
        new(35, "Croatia"),
        new(5, "Cyprus"),
        new(6, "Czech Republic"),
        new(8, "Denmark"),
        new(9, "Estonia"),
        new(11, "Finland"),
        new(12, "France"),
        new(7, "Germany"),
        new(14, "Greece"),
        new(15, "Hungary"),
        new(37, "Iceland"),
        new(16, "Ireland"),
        new(17, "Italy"),
        new(36, "Japan"),
        new(21, "Latvia"),
        new(18, "Liechtenstein"),
        new(19, "Lithuania"),
        new(20, "Luxembourg"),
        new(22, "Malta"),
        new(23, "Netherlands"),
        new(24, "Norway"),
        new(25, "Poland"),
        new(26, "Portugal"),
        new(27, "Romania"),
        new(29, "Singapore"),
        new(31, "Slovakia"),
        new(30, "Slovenia"),
        new(10, "Spain"),
        new(28, "Sweden"),
        new(4, "Switzerland")
    ];

    public static IReadOnlyList<SettingsOptionCatalogItem> Languages { get; } =
    [
        new(1, "English"),
        new(2, "French"),
        new(3, "German"),
        new(4, "Spanish"),
        new(5, "Italian"),
        new(7, "Japanese")
    ];

    public static IReadOnlyList<SettingsOptionCatalogItem> MinConditions { get; } =
    [
        new(1, "Mint"),
        new(2, "Near Mint"),
        new(3, "Excellent"),
        new(4, "Good"),
        new(5, "Light Played"),
        new(6, "Played"),
        new(7, "Poor")
    ];
}

public sealed record SettingsOptionCatalogItem(int Id, string Name);
