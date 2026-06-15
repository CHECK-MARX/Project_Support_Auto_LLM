using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Prompts;

public interface IPromptBuilder
{
    PromptMessages Build(AnswerDraftRequest request);
}
