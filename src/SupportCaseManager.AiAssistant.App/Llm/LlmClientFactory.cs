using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.AiAssistant.App.Llm;

public sealed class LlmClientFactory : ILlmClientFactory
{
    public ILlmClient Create(LlmProviderSettings settings)
    {
        var provider = settings.Provider?.Trim();
        return string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase)
            ? new OllamaClient()
            : new FakeLlmClient();
    }
}
