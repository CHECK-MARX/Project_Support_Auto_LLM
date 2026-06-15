using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Indexing;

public interface IProductScopedIndexService
{
    string GetProductIndexFolder(string aiIndexFolder, string productName);

    Task<AiCaseIndexBuildResult> BuildCaseIndexAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);

    Task<AiManualIndexBuildResult> BuildManualIndexAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);

    Task<AiOfficialDocumentIndexBuildResult> BuildOfficialDocumentIndexAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);
}
