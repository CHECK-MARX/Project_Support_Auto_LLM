using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Llm;

public interface IOllamaConnectionChecker
{
    Task<OllamaConnectionCheckResult> CheckAsync(
        LlmProviderSettings settings,
        bool disableThinking = true,
        CancellationToken cancellationToken = default);
}
