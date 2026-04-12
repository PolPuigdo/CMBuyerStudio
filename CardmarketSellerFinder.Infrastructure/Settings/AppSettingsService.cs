using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Common.Options;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CMBuyerStudio.Infrastructure.Settings;

public sealed class AppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IConfiguration _configuration;
    private readonly string _userSettingsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppSettingsService(IConfiguration configuration, IAppPaths appPaths)
    {
        _configuration = configuration;

        var appRoot = Path.GetDirectoryName(appPaths.CardsPath)
            ?? throw new InvalidOperationException("Unable to resolve application root path.");
        _userSettingsPath = Path.Combine(appRoot, "settings.user.json");
    }

    public async Task<AppSettingsSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var snapshot = BuildBaseSnapshot();
            var userOverride = await ReadUserOverrideAsync(cancellationToken);
            return Merge(snapshot, userOverride);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppSettingsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);

        var payload = ToUserSettingsFile(snapshot);
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_userSettingsPath)!);
            await WriteAtomicAsync(_userSettingsPath, json, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private AppSettingsSnapshot BuildBaseSnapshot()
    {
        var ttlHours = _configuration.GetValue<int?>("Cache:TtlHours") ?? 24;

        var shippingOptions = _configuration.GetSection(ShippingCostsOptions.SectionName)
            .Get<ShippingCostsOptions>() ?? new ShippingCostsOptions();
        var scrapingOptions = _configuration.GetSection(ScrapingOptions.SectionName)
            .Get<ScrapingOptions>() ?? new ScrapingOptions();

        return new AppSettingsSnapshot
        {
            Cache = new CacheSettingsSnapshot
            {
                TtlHours = ttlHours
            },
            ShippingCosts = new ShippingCostsSettingsSnapshot
            {
                Default = shippingOptions.Default,
                Countries = new Dictionary<string, double>(shippingOptions.Countries, StringComparer.OrdinalIgnoreCase)
            },
            Scraping = new ScrapingSettingsSnapshot
            {
                Headless = scrapingOptions.Headless,
                MaxConcurrentWorkers = scrapingOptions.MaxConcurrentWorkers,
                CardmarketUsername = scrapingOptions.CardmarketUsername,
                CardmarketPassword = scrapingOptions.CardmarketPassword,
                UrlProxyChecker = scrapingOptions.UrlProxyCecker,
                SellerCountry = scrapingOptions.SellerCountry,
                Languages = scrapingOptions.Languages,
                MinCondition = scrapingOptions.MinCondition,
                Proxies = scrapingOptions.Proxies
                    .Select(proxy => new ProxySettingsSnapshot
                    {
                        Server = proxy.Server,
                        Username = proxy.Username,
                        Password = proxy.Password
                    })
                    .ToList()
            }
        };
    }

    private async Task<UserSettingsFile?> ReadUserOverrideAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_userSettingsPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_userSettingsPath);
            return await JsonSerializer.DeserializeAsync<UserSettingsFile>(stream, JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static AppSettingsSnapshot Merge(AppSettingsSnapshot baseSnapshot, UserSettingsFile? userOverride)
    {
        if (userOverride is null)
        {
            return baseSnapshot;
        }

        var ttlHours = Math.Max(1, userOverride.Cache?.TtlHours ?? baseSnapshot.Cache.TtlHours);
        var shippingDefault = Math.Max(0, userOverride.ShippingCosts?.Default ?? baseSnapshot.ShippingCosts.Default);
        var shippingCountries = new Dictionary<string, double>(baseSnapshot.ShippingCosts.Countries, StringComparer.OrdinalIgnoreCase);
        if (userOverride.ShippingCosts?.Countries is { Count: > 0 })
        {
            foreach (var countryCost in userOverride.ShippingCosts.Countries)
            {
                shippingCountries[countryCost.Key] = countryCost.Value;
            }
        }

        var scraping = baseSnapshot.Scraping;
        var userScraping = userOverride.Scraping;
        var password = scraping.CardmarketPassword;

        if (!string.IsNullOrWhiteSpace(userScraping?.CardmarketPasswordProtected))
        {
            password = TryDecryptPassword(userScraping.CardmarketPasswordProtected, fallback: password);
        }
        else if (!string.IsNullOrWhiteSpace(userScraping?.CardmarketPassword))
        {
            password = userScraping.CardmarketPassword;
        }

        return new AppSettingsSnapshot
        {
            Cache = new CacheSettingsSnapshot
            {
                TtlHours = ttlHours
            },
            ShippingCosts = new ShippingCostsSettingsSnapshot
            {
                Default = shippingDefault,
                Countries = shippingCountries
            },
            Scraping = new ScrapingSettingsSnapshot
            {
                Headless = userScraping?.Headless ?? scraping.Headless,
                MaxConcurrentWorkers = Math.Max(1, userScraping?.MaxConcurrentWorkers ?? scraping.MaxConcurrentWorkers),
                CardmarketUsername = userScraping?.CardmarketUsername ?? scraping.CardmarketUsername,
                CardmarketPassword = password,
                UrlProxyChecker = userScraping?.UrlProxyChecker ?? scraping.UrlProxyChecker,
                SellerCountry = userScraping?.SellerCountry ?? scraping.SellerCountry,
                Languages = userScraping?.Languages ?? scraping.Languages,
                MinCondition = Math.Max(1, userScraping?.MinCondition ?? scraping.MinCondition),
                Proxies = userScraping?.Proxies?.Select(proxy => new ProxySettingsSnapshot
                {
                    Server = proxy.Server ?? string.Empty,
                    Username = proxy.Username,
                    Password = proxy.Password
                }).ToList()
                ?? scraping.Proxies
            }
        };
    }

    private static UserSettingsFile ToUserSettingsFile(AppSettingsSnapshot snapshot)
    {
        return new UserSettingsFile
        {
            Cache = new CacheSection
            {
                TtlHours = snapshot.Cache.TtlHours
            },
            ShippingCosts = new ShippingCostsSection
            {
                Default = snapshot.ShippingCosts.Default,
                Countries = snapshot.ShippingCosts.Countries
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            },
            Scraping = new ScrapingSection
            {
                CardmarketUsername = snapshot.Scraping.CardmarketUsername,
                CardmarketPasswordProtected = EncryptPassword(snapshot.Scraping.CardmarketPassword),
                SellerCountry = snapshot.Scraping.SellerCountry,
                Languages = snapshot.Scraping.Languages,
                MinCondition = snapshot.Scraping.MinCondition,
                Proxies = snapshot.Scraping.Proxies.Select(proxy => new ProxySection
                {
                    Server = proxy.Server,
                    Username = proxy.Username,
                    Password = proxy.Password
                }).ToList()
            }
        };
    }

    private static void Validate(AppSettingsSnapshot snapshot)
    {
        if (snapshot.Cache.TtlHours < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Cache.TtlHours must be >= 1.");
        }

        if (snapshot.ShippingCosts.Default < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "ShippingCosts.Default must be >= 0.");
        }

        foreach (var pair in snapshot.ShippingCosts.Countries)
        {
            if (pair.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(snapshot), $"ShippingCosts.{pair.Key} must be >= 0.");
            }
        }

        if (snapshot.Scraping.MinCondition < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Scraping.MinCondition must be >= 1.");
        }

        if (snapshot.Scraping.MaxConcurrentWorkers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Scraping.MaxConcurrentWorkers must be >= 1.");
        }

        foreach (var proxy in snapshot.Scraping.Proxies)
        {
            if (string.IsNullOrWhiteSpace(proxy.Server))
            {
                throw new ArgumentException("Each proxy requires a Server value.", nameof(snapshot));
            }
        }
    }

    private static string EncryptPassword(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string TryDecryptPassword(string protectedBase64, string fallback)
    {
        try
        {
            var encrypted = Convert.FromBase64String(protectedBase64);
            var bytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return fallback;
        }
    }

    private static async Task WriteAtomicAsync(string destinationPath, string json, CancellationToken cancellationToken)
    {
        var tempPath = destinationPath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);

            if (!File.Exists(destinationPath))
            {
                File.Move(tempPath, destinationPath);
                return;
            }

            try
            {
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
            }
            catch
            {
                File.Copy(tempPath, destinationPath, overwrite: true);
                File.Delete(tempPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed class UserSettingsFile
    {
        public CacheSection? Cache { get; init; }

        public ShippingCostsSection? ShippingCosts { get; init; }

        public ScrapingSection? Scraping { get; init; }
    }

    private sealed class CacheSection
    {
        public int? TtlHours { get; init; }
    }

    private sealed class ShippingCostsSection
    {
        public double? Default { get; init; }

        public Dictionary<string, double>? Countries { get; init; }
    }

    private sealed class ScrapingSection
    {
        public bool? Headless { get; init; }

        public int? MaxConcurrentWorkers { get; init; }

        public string? CardmarketUsername { get; init; }

        public string? CardmarketPassword { get; init; }

        public string? CardmarketPasswordProtected { get; init; }

        public string? UrlProxyChecker { get; init; }

        public string? SellerCountry { get; init; }

        public string? Languages { get; init; }

        public int? MinCondition { get; init; }

        public List<ProxySection>? Proxies { get; init; }
    }

    private sealed class ProxySection
    {
        public string? Server { get; init; }

        public string? Username { get; init; }

        public string? Password { get; init; }
    }
}
