using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Core.Llm;

public interface ILlmClient
{
    Task<LlmGenerationResult> GenerateAsync(
        PromptMessages messages,
        LlmProviderSettings settings,
        bool disableThinking = true,
        CancellationToken cancellationToken = default);
}
