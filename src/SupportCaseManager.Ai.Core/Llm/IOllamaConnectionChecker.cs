using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Llm;

public interface IOllamaConnectionChecker
{
    Task<OllamaConnectionCheckResult> CheckAsync(
        LlmProviderSettings settings,
        bool disableThinking = true,
        CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<string>> ListModelsAsync(
        LlmProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        return (await CheckAsync(settings, cancellationToken: cancellationToken)).AvailableModels;
    }
}
