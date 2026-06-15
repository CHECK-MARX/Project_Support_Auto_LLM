using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Indexing;

public interface IAiOfficialDocumentIndexBuilder
{
    Task<AiOfficialDocumentIndexBuildResult> BuildAsync(
        ProductKnowledgeSettings product,
        string indexFolder,
        CancellationToken cancellationToken = default);
}
