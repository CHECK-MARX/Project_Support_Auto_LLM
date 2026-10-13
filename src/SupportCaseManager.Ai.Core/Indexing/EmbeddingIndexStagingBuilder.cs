namespace SupportCaseManager.Ai.Core.Indexing;

/// <summary>
/// Builds a disposable embedding index from an existing product index without
/// modifying that source index or the application's active retrieval path.
/// </summary>
public sealed class EmbeddingIndexStagingBuilder
{
    private readonly EmbeddingIndexUpdater updater;
    private readonly IEmbeddingModelDigestResolver digestResolver;

    public EmbeddingIndexStagingBuilder(
        EmbeddingIndexUpdater? updater = null,
        HttpClient? httpClient = null)
    {
        this.updater = updater ?? new EmbeddingIndexUpdater();
        digestResolver = new OllamaEmbeddingModelDigestResolver(httpClient);
    }

    public async Task<EmbeddingIndexUpdateResult> BuildAsync(
        string productName,
        string sourceProductIndexFolder,
        string stagingRoot,
        string endpoint,
        string embeddingModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProductIndexFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (!Directory.Exists(sourceProductIndexFolder))
        {
            throw new DirectoryNotFoundException($"Product source index was not found: {sourceProductIndexFolder}");
        }

        var stagingProductFolder = Path.Combine(stagingRoot, productName);
        Directory.CreateDirectory(stagingProductFolder);
        var digest = await digestResolver.ResolveAsync(endpoint, embeddingModel, cancellationToken);
        if (string.IsNullOrWhiteSpace(digest))
        {
            return new EmbeddingIndexUpdateResult
            {
                EmbeddingModel = embeddingModel,
                IndexFilePath = Path.Combine(stagingProductFolder, EmbeddingIndexDocument.FileName),
                Status = "Failed",
                Warning = "Ollamaの埋め込みモデルdigestを確認できません。ステージングindexは更新していません。",
            };
        }

        return await updater.UpdateAsync(
            productName,
            stagingProductFolder,
            endpoint,
            embeddingModel,
            forceRebuild: true,
            cancellationToken,
            sourceProductIndexFolder,
            digest,
            sanitizeEmbeddingInput: true);
    }
}
