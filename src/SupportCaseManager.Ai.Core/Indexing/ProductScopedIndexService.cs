using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed class ProductScopedIndexService : IProductScopedIndexService
{
    private readonly IAiCaseIndexBuilder caseIndexBuilder;
    private readonly IAiManualIndexBuilder manualIndexBuilder;
    private readonly IAiOfficialDocumentIndexBuilder officialDocumentIndexBuilder;

    public ProductScopedIndexService(
        IAiCaseIndexBuilder caseIndexBuilder,
        IAiManualIndexBuilder manualIndexBuilder,
        IAiOfficialDocumentIndexBuilder? officialDocumentIndexBuilder = null)
    {
        this.caseIndexBuilder = caseIndexBuilder ?? throw new ArgumentNullException(nameof(caseIndexBuilder));
        this.manualIndexBuilder = manualIndexBuilder ?? throw new ArgumentNullException(nameof(manualIndexBuilder));
        this.officialDocumentIndexBuilder = officialDocumentIndexBuilder ?? new AiOfficialDocumentIndexBuilder();
    }

    public string GetProductIndexFolder(string aiIndexFolder, string productName)
    {
        return ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
    }

    public async Task<AiCaseIndexBuildResult> BuildCaseIndexAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = GetProductIndexFolder(aiIndexFolder, product.ProductName);
        return await caseIndexBuilder.BuildAsync(product.CloseFolder, productIndexFolder, cancellationToken);
    }

    public async Task<AiManualIndexBuildResult> BuildManualIndexAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = GetProductIndexFolder(aiIndexFolder, product.ProductName);
        return await manualIndexBuilder.BuildManyAsync(product.ManualFolders, productIndexFolder, cancellationToken);
    }

    public async Task<AiOfficialDocumentIndexBuildResult> BuildOfficialDocumentIndexAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        return await officialDocumentIndexBuilder.BuildAsync(product, aiIndexFolder, cancellationToken);
    }
}
