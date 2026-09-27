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

    Task<IReadOnlyList<string>> ListChatModelsAsync(
        LlmProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        return ListModelsAsync(settings, cancellationToken);
    }

    Task<OllamaModelCapabilityResult> CheckChatModelCapabilityAsync(
        LlmProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OllamaModelCapabilityResult { CanGenerate = true });
    }
}
