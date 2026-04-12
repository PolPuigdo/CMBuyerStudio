using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Domain.Market;
using CMBuyerStudio.Infrastructure.Cardmarket.Cache;
using System.Text.Json;

namespace CMBuyerStudio.Infrastructure.Caching;

public sealed class MarketDataCacheService : IMarketDataCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IAppSettingsService _appSettingsService;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MarketDataCacheService(IAppSettingsService appSettingsService, IAppPaths paths)
    {
        _appSettingsService = appSettingsService;
        _cachePath = Path.Combine(paths.CardsCachePath, "market-data-cache.json");
    }

    public async Task<MarketCardData?> GetAsync(ScrapingTarget target, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            if (entries.Count == 0)
            {
                return null;
            }

            var entry = entries.FirstOrDefault(x =>
                string.Equals(x.ProductUrl, target.ProductUrl, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return null;
            }

            var ttl = await GetTtlAsync(cancellationToken);
            var expiresAtUtc = entry.CachedAtUtc.Add(ttl);

            if (DateTime.UtcNow > expiresAtUtc)
            {
                return null;
            }

            return entry.Data;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(MarketCardData marketData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketData);
        ArgumentNullException.ThrowIfNull(marketData.Target);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);

            var entries = await LoadEntriesAsync(cancellationToken);

            var existingEntry = entries.FirstOrDefault(x =>
                string.Equals(x.ProductUrl, marketData.Target.ProductUrl, StringComparison.OrdinalIgnoreCase));

            var newEntry = new MarketCacheEntry
            {
                ProductUrl = marketData.Target.ProductUrl,
                CachedAtUtc = DateTime.UtcNow,
                Data = marketData
            };

            if (existingEntry is null)
            {
                entries.Add(newEntry);
            }
            else
            {
                existingEntry.ProductUrl = newEntry.ProductUrl;
                existingEntry.CachedAtUtc = newEntry.CachedAtUtc;
                existingEntry.Data = newEntry.Data;
            }

            await WriteEntriesAtomicAsync(entries, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TimeSpan> GetTtlAsync(CancellationToken cancellationToken)
    {
        var settings = await _appSettingsService.GetCurrentAsync(cancellationToken);
        var ttlHours = Math.Max(1, settings.Cache.TtlHours);
        return TimeSpan.FromHours(ttlHours);
    }

    private async Task<List<MarketCacheEntry>> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_cachePath);

            var entries = await JsonSerializer.DeserializeAsync<List<MarketCacheEntry>>(
                stream,
                JsonOptions,
                cancellationToken);

            return entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task WriteEntriesAtomicAsync(List<MarketCacheEntry> entries, CancellationToken cancellationToken)
    {
        var tempPath = _cachePath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
        }

        if (File.Exists(_cachePath))
        {
            File.Delete(_cachePath);
        }

        File.Move(tempPath, _cachePath);
    }
}
