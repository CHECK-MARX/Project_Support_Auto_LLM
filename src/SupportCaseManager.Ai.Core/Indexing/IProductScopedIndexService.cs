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

    Task<KnowledgeIndexStatus> InspectKnowledgeAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new KnowledgeIndexStatus { ProductName = product.ProductName });
    }

    Task<KnowledgeUpdateResult> UpdateKnowledgeAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        KnowledgeUpdateScope scope = KnowledgeUpdateScope.All,
        bool forceRebuild = false,
        string? embeddingModel = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new KnowledgeUpdateResult());
    }

    Task<KnowledgeUpdateResult> UpdateKnowledgeWithEmbeddingsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        KnowledgeUpdateScope scope,
        bool forceRebuild,
        string? embeddingModel,
        string? embeddingEndpoint,
        CancellationToken cancellationToken = default)
    {
        return UpdateKnowledgeAsync(
            product,
            aiIndexFolder,
            scope,
            forceRebuild,
            embeddingModel,
            cancellationToken);
    }
}
