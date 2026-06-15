using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Settings;

public interface IAiSettingsStore
{
    Task<AiAssistantSettings> LoadAsync(string aiDataFolder, CancellationToken cancellationToken = default);

    Task SaveAsync(AiAssistantSettings settings, CancellationToken cancellationToken = default);
}
