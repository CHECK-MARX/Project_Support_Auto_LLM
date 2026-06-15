using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Answers;

public interface IAiAnswerService
{
    Task<AnswerDraftResult> GenerateDraftAsync(
        AnswerDraftRequest request,
        CancellationToken cancellationToken = default);
}
