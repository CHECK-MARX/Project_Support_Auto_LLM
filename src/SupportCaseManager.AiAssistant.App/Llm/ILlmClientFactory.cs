using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.AiAssistant.App.Llm;

public interface ILlmClientFactory
{
    ILlmClient Create(LlmProviderSettings settings);
}
