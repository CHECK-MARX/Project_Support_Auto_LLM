using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Evidence;

public interface IEvidenceBuilder
{
    IReadOnlyList<EvidenceItem> BuildEvidence(AnswerDraftRequest request);

    double CalculateConfidence(AnswerDraftRequest request, IReadOnlyList<EvidenceItem> evidence);
}
