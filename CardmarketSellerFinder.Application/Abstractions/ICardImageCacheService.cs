namespace CMBuyerStudio.Application.Abstractions;

public interface ICardImageCacheService
{
    Task<string> GetOrDownloadAsync(string imageUrl, string imageName, CancellationToken cancellationToken = default);
}
