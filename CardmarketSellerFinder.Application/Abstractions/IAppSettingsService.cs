using CMBuyerStudio.Application.Models;

namespace CMBuyerStudio.Application.Abstractions;

public interface IAppSettingsService
{
    Task<AppSettingsSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettingsSnapshot snapshot, CancellationToken cancellationToken = default);
}
