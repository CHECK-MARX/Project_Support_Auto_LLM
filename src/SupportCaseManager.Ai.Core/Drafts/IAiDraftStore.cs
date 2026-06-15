using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Drafts;

public interface IAiDraftStore
{
    Task<string> SaveAsync(
        AnswerDraftRequest request,
        AnswerDraftResult result,
        CancellationToken cancellationToken = default);
}
