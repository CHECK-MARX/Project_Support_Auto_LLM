using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.App.AiHandoff;

public interface IAiAssistantLaunchContextBuilder
{
    AiAssistantLaunchContext BuildFromCurrentState(AiAssistantCurrentState state);
}
