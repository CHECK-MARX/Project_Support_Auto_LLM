using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;
using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed class ProductScopedIndexService : IProductScopedIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan OfficialDocsRefreshAge = TimeSpan.FromDays(7);
    private readonly IAiCaseIndexBuilder caseIndexBuilder;
    private readonly IAiManualIndexBuilder manualIndexBuilder;
    private readonly IAiOfficialDocumentIndexBuilder officialDocumentIndexBuilder;
    private readonly EmbeddingIndexUpdater embeddingIndexUpdater;
    private readonly SemaphoreSlim updateLock = new(1, 1);

    public ProductScopedIndexService(
        IAiCaseIndexBuilder caseIndexBuilder,
        IAiManualIndexBuilder manualIndexBuilder,
        IAiOfficialDocumentIndexBuilder? officialDocumentIndexBuilder = null,
        IOllamaEmbeddingClient? embeddingClient = null)
    {
        this.caseIndexBuilder = caseIndexBuilder ?? throw new ArgumentNullException(nameof(caseIndexBuilder));
        this.manualIndexBuilder = manualIndexBuilder ?? throw new ArgumentNullException(nameof(manualIndexBuilder));
        this.officialDocumentIndexBuilder = officialDocumentIndexBuilder ?? new AiOfficialDocumentIndexBuilder();
        embeddingIndexUpdater = new EmbeddingIndexUpdater(embeddingClient);
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
        return await caseIndexBuilder.BuildForProductAsync(
            product.CloseFolder,
            productIndexFolder,
            product.ProductName,
            cancellationToken);
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

    public async Task<KnowledgeIndexStatus> InspectKnowledgeAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var casePath = Path.Combine(productIndexFolder, AiCaseIndexBuilder.IndexFileName);
        var manualPath = Path.Combine(productIndexFolder, AiManualIndexBuilder.IndexFileName);
        var officialPath = Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName);
        var anyIndex = File.Exists(casePath) || File.Exists(manualPath) || File.Exists(officialPath);
        if (!anyIndex)
        {
            return new KnowledgeIndexStatus
            {
                ProductName = product.ProductName,
                Status = KnowledgeStatuses.NotCreated,
                Message = "ナレッジは未作成です。",
            };
        }

        var manifest = await KnowledgeManifestStore.LoadAsync(productIndexFolder, cancellationToken);
        if (manifest is { SchemaVersion: KnowledgeManifest.CurrentSchemaVersion } &&
            string.Equals(manifest.ChunkingVersion, KnowledgeManifest.CurrentChunkingVersion, StringComparison.Ordinal))
        {
            var missingExpectedIndex =
                (!string.IsNullOrWhiteSpace(product.CloseFolder) && !File.Exists(casePath)) ||
                (product.ManualFolders.Count > 0 && !File.Exists(manualPath)) ||
                (product.DocumentUrls.Count > 0 && !File.Exists(officialPath));
            var manifestStatus = missingExpectedIndex ? KnowledgeStatuses.Warning : KnowledgeStatuses.Ready;
            var manifestMessage = missingExpectedIndex
                ? "manifestに対応するインデックスファイルが不足しています。ナレッジを更新してください。"
                : "既存インデックスをmanifestから確認しました。";
            var manifestOfficialUpdatedAt = GetOfficialDocsLastUpdatedAt(manifest, null);
            if (manifestOfficialUpdatedAt is { } manifestUpdatedAt &&
                product.DocumentUrls.Count > 0 &&
                DateTimeOffset.Now - manifestUpdatedAt > OfficialDocsRefreshAge)
            {
                manifestStatus = KnowledgeStatuses.UpdateAvailable;
                manifestMessage = "既存インデックスを利用できます。公式Docsの更新候補があります。";
            }

            return new KnowledgeIndexStatus
            {
                ProductName = product.ProductName,
                Status = manifestStatus,
                LastUpdatedAt = manifest.LastSuccessfulUpdate ?? manifest.IndexedAt,
                ManualDocumentCount = SumSourceCount(manifest, "Manual", static source => source.DocumentCount),
                ManualChunkCount = SumSourceCount(manifest, "Manual", static source => source.ChunkCount),
                OfficialDocumentCount = SumSourceCount(manifest, "OfficialDoc", static source => source.DocumentCount),
                OfficialChunkCount = SumSourceCount(manifest, "OfficialDoc", static source => source.ChunkCount),
                PastCaseCount = SumSourceCount(manifest, "PastCaseNote", static source => source.DocumentCount),
                PastCaseChunkCount = SumSourceCount(manifest, "PastCaseNote", static source => source.ChunkCount),
                UsedExistingIndex = true,
                Message = manifestMessage,
            };
        }

        var caseIndex = await ReadJsonAsync<AiIndexDocument>(casePath, cancellationToken);
        var manualIndex = await ReadJsonAsync<AiManualIndexDocument>(manualPath, cancellationToken);
        var officialIndex = await ReadJsonAsync<AiOfficialDocumentIndexDocument>(officialPath, cancellationToken);
        var status = KnowledgeStatuses.Warning;
        var message = manifest is null
            ? "既存インデックスを利用できます。次回更新時にmanifestを作成します。"
            : "manifestの互換性を確認できません。次回更新時に再作成します。";

        var officialDocsLastUpdatedAt = GetOfficialDocsLastUpdatedAt(manifest, officialIndex);
        if (officialDocsLastUpdatedAt is { } updatedAt &&
            product.DocumentUrls.Count > 0 &&
            DateTimeOffset.Now - updatedAt > OfficialDocsRefreshAge)
        {
            status = KnowledgeStatuses.UpdateAvailable;
            message = "既存インデックスを利用中です。公式Docsの更新候補があります。";
        }

        return new KnowledgeIndexStatus
        {
            ProductName = product.ProductName,
            Status = status,
            LastUpdatedAt = manifest?.LastSuccessfulUpdate
                ?? Latest(caseIndex?.BuiltAt, manualIndex?.BuiltAt, officialIndex?.BuiltAt),
            ManualDocumentCount = manualIndex?.Manuals.Select(static item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,
            ManualChunkCount = manualIndex?.Manuals.Count ?? 0,
            OfficialDocumentCount = officialIndex?.Documents.Select(static item => item.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,
            OfficialChunkCount = officialIndex?.Documents.Count ?? 0,
            PastCaseCount = caseIndex?.Notes.Select(static item => item.CaseFolderPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,
            PastCaseChunkCount = caseIndex?.Notes.Count ?? 0,
            UsedExistingIndex = true,
            Message = message,
        };
    }

    public Task<KnowledgeUpdateResult> UpdateKnowledgeAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        KnowledgeUpdateScope scope = KnowledgeUpdateScope.All,
        bool forceRebuild = false,
        string? embeddingModel = null,
        CancellationToken cancellationToken = default)
    {
        return UpdateKnowledgeCoreAsync(
            product,
            aiIndexFolder,
            scope,
            forceRebuild,
            embeddingModel,
            embeddingEndpoint: null,
            cancellationToken);
    }

    public Task<KnowledgeUpdateResult> UpdateKnowledgeWithEmbeddingsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        KnowledgeUpdateScope scope,
        bool forceRebuild,
        string? embeddingModel,
        string? embeddingEndpoint,
        CancellationToken cancellationToken = default)
    {
        return UpdateKnowledgeCoreAsync(
            product,
            aiIndexFolder,
            scope,
            forceRebuild,
            embeddingModel,
            embeddingEndpoint,
            cancellationToken);
    }

    private async Task<KnowledgeUpdateResult> UpdateKnowledgeCoreAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        KnowledgeUpdateScope scope,
        bool forceRebuild,
        string? embeddingModel,
        string? embeddingEndpoint,
        CancellationToken cancellationToken)
    {
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            ArgumentNullException.ThrowIfNull(product);
            var productIndexFolder = GetProductIndexFolder(aiIndexFolder, product.ProductName);
            Directory.CreateDirectory(productIndexFolder);
            var oldManifest = await KnowledgeManifestStore.LoadAsync(productIndexFolder, cancellationToken);
            var requiresFullRebuild = forceRebuild || RequiresFullRebuild(oldManifest);
            AiCaseIndexBuildResult? caseResult = null;
            AiManualIndexBuildResult? manualResult = null;
            AiOfficialDocumentIndexBuildResult? officialResult = null;
            EmbeddingIndexUpdateResult? embeddingResult = null;

            if (scope.HasFlag(KnowledgeUpdateScope.PastCases) && !string.IsNullOrWhiteSpace(product.CloseFolder))
            {
                caseResult = await caseIndexBuilder.BuildIncrementalForProductAsync(
                    product.CloseFolder,
                    productIndexFolder,
                    product.ProductName,
                    requiresFullRebuild,
                    cancellationToken);
            }

            if (scope.HasFlag(KnowledgeUpdateScope.Manuals) && product.ManualFolders.Count > 0)
            {
                manualResult = await manualIndexBuilder.BuildManyIncrementalAsync(
                    product.ManualFolders,
                    productIndexFolder,
                    requiresFullRebuild,
                    cancellationToken);
            }

            if (scope.HasFlag(KnowledgeUpdateScope.OfficialDocs) &&
                product.DocumentUrls.Count > 0 &&
                ShouldRefreshOfficialDocs(product, productIndexFolder, oldManifest, scope, requiresFullRebuild))
            {
                officialResult = await officialDocumentIndexBuilder.BuildAsync(product, aiIndexFolder, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(embeddingModel) &&
                !string.IsNullOrWhiteSpace(embeddingEndpoint))
            {
                embeddingResult = await embeddingIndexUpdater.UpdateAsync(
                    product.ProductName,
                    productIndexFolder,
                    embeddingEndpoint,
                    embeddingModel,
                    forceRebuild,
                    cancellationToken);
            }

            var hasErrors = (caseResult?.ErrorCount ?? 0) > 0
                || (manualResult?.ErrorCount ?? 0) > 0
                || (officialResult?.FetchFailureCount ?? 0) > 0;
            var status = await InspectKnowledgeAsync(product, aiIndexFolder, cancellationToken);
            var now = DateTimeOffset.Now;
            var manifestEmbeddingModel = embeddingResult switch
            {
                { IsSuccess: true } => embeddingResult.EmbeddingModel,
                { IsSuccess: false } => oldManifest?.EmbeddingModel ?? string.Empty,
                _ => oldManifest?.EmbeddingModel ?? string.Empty,
            };
            var manifest = await BuildManifestAsync(
                product,
                productIndexFolder,
                manifestEmbeddingModel,
                hasErrors ? oldManifest?.LastSuccessfulUpdate : now,
                hasErrors ? "Warning" : "Success",
                cancellationToken);
            await KnowledgeManifestStore.SaveAtomicallyAsync(productIndexFolder, manifest, cancellationToken);

            status = status with
            {
                Status = hasErrors ? KnowledgeStatuses.Warning : KnowledgeStatuses.Ready,
                LastUpdatedAt = hasErrors ? status.LastUpdatedAt : now,
                Message = hasErrors
                    ? "一部の更新に失敗しました。利用可能な前回インデックスを維持しています。"
                    : BuildUpdateMessage(caseResult, manualResult, officialResult, embeddingResult),
            };

            return new KnowledgeUpdateResult
            {
                Status = status,
                PastCases = caseResult,
                Manuals = manualResult,
                OfficialDocs = officialResult,
                Embeddings = embeddingResult,
            };
        }
        finally
        {
            updateLock.Release();
        }
    }

    private static async Task<KnowledgeManifest> BuildManifestAsync(
        ProductKnowledgeSettings product,
        string productIndexFolder,
        string? embeddingModel,
        DateTimeOffset? lastSuccessfulUpdate,
        string result,
        CancellationToken cancellationToken)
    {
        var caseIndex = await ReadJsonAsync<AiIndexDocument>(Path.Combine(productIndexFolder, AiCaseIndexBuilder.IndexFileName), cancellationToken);
        var manualIndex = await ReadJsonAsync<AiManualIndexDocument>(Path.Combine(productIndexFolder, AiManualIndexBuilder.IndexFileName), cancellationToken);
        var officialIndex = await ReadJsonAsync<AiOfficialDocumentIndexDocument>(Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName), cancellationToken);
        var indexedAt = Latest(caseIndex?.BuiltAt, manualIndex?.BuiltAt, officialIndex?.BuiltAt);
        var sources = new List<KnowledgeManifestSource>();

        if (!string.IsNullOrWhiteSpace(product.CloseFolder))
        {
            sources.Add(BuildFolderSource(
                product.CloseFolder,
                "PastCaseNote",
                caseIndex?.Notes.Select(static note => note.NoteFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,
                caseIndex?.Notes.Count ?? 0,
                caseIndex?.BuiltAt));
        }

        sources.AddRange(product.ManualFolders.Select(folder => BuildFolderSource(
            folder,
            "Manual",
            manualIndex?.Manuals.Where(item => IsUnderFolder(item.FilePath, folder)).Select(static item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,
            manualIndex?.Manuals.Count(item => IsUnderFolder(item.FilePath, folder)) ?? 0,
            manualIndex?.BuiltAt)));

        foreach (var url in product.DocumentUrls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matching = officialIndex?.Documents
                .Where(document => document.Url.StartsWith(url, StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];
            sources.Add(new KnowledgeManifestSource
            {
                SourcePathOrUrl = url,
                SourceType = "OfficialDoc",
                ContentHash = KnowledgeManifestStore.BuildSourceFingerprint(matching.Select(static document => document.ContentHash)),
                IndexedAt = officialIndex?.BuiltAt,
                DocumentCount = matching.Select(static document => document.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ChunkCount = matching.Count,
            });
        }

        return new KnowledgeManifest
        {
            ProductName = product.ProductName,
            SourcePathOrUrl = productIndexFolder,
            LastModified = sources.Select(static source => source.LastModified).Where(static value => value.HasValue).Max(),
            ContentHash = KnowledgeManifestStore.BuildSourceFingerprint(sources.Select(static source => $"{source.SourceType}|{source.SourcePathOrUrl}|{source.ContentHash}")),
            IndexedAt = indexedAt,
            EmbeddingModel = embeddingModel ?? string.Empty,
            DocumentCount = sources.Sum(static source => source.DocumentCount),
            ChunkCount = sources.Sum(static source => source.ChunkCount),
            LastSuccessfulUpdate = lastSuccessfulUpdate,
            LastUpdateResult = result,
            Sources = sources,
        };
    }

    private static KnowledgeManifestSource BuildFolderSource(
        string folder,
        string sourceType,
        int documentCount,
        int chunkCount,
        DateTimeOffset? indexedAt)
    {
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToList()
            : [];
        return new KnowledgeManifestSource
        {
            SourcePathOrUrl = folder,
            SourceType = sourceType,
            LastModified = files.Count == 0 ? null : files.Max(static file => (DateTimeOffset)file.LastWriteTime),
            ContentHash = KnowledgeManifestStore.BuildSourceFingerprint(
                files.Select(static file => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")),
            IndexedAt = indexedAt,
            DocumentCount = documentCount,
            ChunkCount = chunkCount,
        };
    }

    private static bool IsUnderFolder(string filePath, string folder)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values)
    {
        return values.Where(static value => value.HasValue).Max();
    }

    private static string BuildUpdateMessage(
        AiCaseIndexBuildResult? cases,
        AiManualIndexBuildResult? manuals,
        AiOfficialDocumentIndexBuildResult? official,
        EmbeddingIndexUpdateResult? embeddings)
    {
        var parts = new List<string>();
        if (cases is not null)
        {
            parts.Add($"PastCase 追加{cases.AddedCaseCount}/変更{cases.ChangedCaseCount}/削除{cases.DeletedCaseCount}/未変更{cases.UnchangedCaseCount}");
        }

        if (manuals is not null)
        {
            parts.Add($"Manual 追加{manuals.AddedFileCount}/変更{manuals.ChangedFileCount}/削除{manuals.DeletedFileCount}/未変更{manuals.UnchangedFileCount}");
        }

        if (official is not null)
        {
            parts.Add($"OfficialDoc {official.IndexedChunkCount}チャンク");
        }

        if (embeddings is { IsSuccess: true })
        {
            parts.Add($"Embedding 追加{embeddings.AddedCount}/変更{embeddings.ChangedCount}/削除{embeddings.DeletedCount}/未変更{embeddings.UnchangedCount}");
        }
        else if (embeddings is { IsSuccess: false })
        {
            parts.Add(embeddings.Warning);
        }

        return parts.Count == 0 ? "変更はありません。既存インデックスを利用します。" : string.Join(" / ", parts);
    }

    private static bool ShouldRefreshOfficialDocs(
        ProductKnowledgeSettings product,
        string productIndexFolder,
        KnowledgeManifest? manifest,
        KnowledgeUpdateScope scope,
        bool forceRebuild)
    {
        if (forceRebuild || scope == KnowledgeUpdateScope.OfficialDocs)
        {
            return true;
        }

        if (!File.Exists(Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName)))
        {
            return true;
        }

        var oldUrls = manifest?.Sources
            .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
            .Select(static source => source.SourcePathOrUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var currentUrls = product.DocumentUrls
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Select(static url => url.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!oldUrls.SetEquals(currentUrls))
        {
            return true;
        }

        var officialLastUpdate = manifest?.Sources
            .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
            .Select(static source => source.IndexedAt)
            .Where(static value => value.HasValue)
            .Max();
        return officialLastUpdate is not { } lastUpdate
            || DateTimeOffset.Now - lastUpdate > OfficialDocsRefreshAge;
    }

    private static bool RequiresFullRebuild(KnowledgeManifest? manifest)
    {
        if (manifest is null)
        {
            return false;
        }

        return manifest.SchemaVersion != KnowledgeManifest.CurrentSchemaVersion
            || !string.Equals(
                manifest.ChunkingVersion,
                KnowledgeManifest.CurrentChunkingVersion,
                StringComparison.Ordinal);
    }

    private static DateTimeOffset? GetOfficialDocsLastUpdatedAt(
        KnowledgeManifest? manifest,
        AiOfficialDocumentIndexDocument? officialIndex)
    {
        return manifest?.Sources
            .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
            .Select(static source => source.IndexedAt)
            .Where(static value => value.HasValue)
            .Max()
            ?? officialIndex?.BuiltAt;
    }

    private static int SumSourceCount(
        KnowledgeManifest manifest,
        string sourceType,
        Func<KnowledgeManifestSource, int> selector)
    {
        return manifest.Sources
            .Where(source => string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase))
            .Sum(selector);
    }
}
