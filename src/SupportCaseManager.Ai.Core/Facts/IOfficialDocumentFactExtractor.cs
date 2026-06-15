using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Facts;

public interface IOfficialDocumentFactExtractor
{
    IReadOnlyList<CandidateFact> Extract(AiOfficialDocumentIndexDocument document);
}
