namespace CMBuyerStudio.Application.Models;

public sealed class AppSettingsSnapshot
{
    public CacheSettingsSnapshot Cache { get; init; } = new();

    public ShippingCostsSettingsSnapshot ShippingCosts { get; init; } = new();

    public ScrapingSettingsSnapshot Scraping { get; init; } = new();
}

public sealed class CacheSettingsSnapshot
{
    public int TtlHours { get; init; } = 24;
}

public sealed class ShippingCostsSettingsSnapshot
{
    public double Default { get; init; } = 3.0;

    public Dictionary<string, double> Countries { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ScrapingSettingsSnapshot
{
    public bool Headless { get; init; }

    public int MaxConcurrentWorkers { get; init; } = 10;

    public string CardmarketUsername { get; init; } = string.Empty;

    public string CardmarketPassword { get; init; } = string.Empty;

    public string UrlProxyChecker { get; init; } = string.Empty;

    public string SellerCountry { get; init; } =
        "1,2,3,35,5,6,8,9,11,12,7,14,15,16,17,21,19,20,22,23,25,26,27,31,30,10,28";

    public string Languages { get; init; } = "1";

    public int MinCondition { get; init; } = 2;

    public List<ProxySettingsSnapshot> Proxies { get; init; } = [];
}

public sealed class ProxySettingsSnapshot
{
    public string Server { get; init; } = string.Empty;

    public string? Username { get; init; }

    public string? Password { get; init; }
}
