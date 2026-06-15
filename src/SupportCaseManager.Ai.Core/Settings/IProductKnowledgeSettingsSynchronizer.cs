using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Settings;

public interface IProductKnowledgeSettingsSynchronizer
{
    AiAssistantSettings Synchronize(
        AiAssistantSettings currentAiSettings,
        IReadOnlyList<SupportToolProductSettings> supportToolProducts);
}
