using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Settings;

public interface ISupportToolSettingsReader
{
    string? FindDefaultSettingsFilePath();

    Task<IReadOnlyList<SupportToolProductSettings>> ReadProductsAsync(
        string userSettingsFilePath,
        CancellationToken cancellationToken = default);
}
