using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class ProductScopedIndexTests
{
    [Fact]
    public async Task BuildManualIndexAsync_WritesManualsIndexUnderProductFolder()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "license.md"), "# License\r\nlicense server port", Encoding.UTF8);
        var service = CreateService();

        var result = await service.BuildManualIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "HelixQAC",
                ManualFolders = [manualFolder],
            },
            Path.Combine(temp.Path, "ai-index"));

        var expectedProductFolder = Path.Combine(temp.Path, "ai-index", "products", "HelixQAC");
        Assert.Equal(Path.Combine(expectedProductFolder, AiManualIndexBuilder.IndexFileName), result.IndexFilePath);
        Assert.True(File.Exists(result.IndexFilePath));
        var document = await ReadManualIndexAsync(result.IndexFilePath);
        Assert.Single(document.Manuals);
    }

    [Fact]
    public async Task BuildManualIndexAsync_IndexesMultipleManualFolders()
    {
        using var temp = new TempDirectory();
        var manualFolder1 = Path.Combine(temp.Path, "manuals1");
        var manualFolder2 = Path.Combine(temp.Path, "manuals2");
        Directory.CreateDirectory(manualFolder1);
        Directory.CreateDirectory(manualFolder2);
        await File.WriteAllTextAsync(Path.Combine(manualFolder1, "license.md"), "# License\r\nlicense server", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(manualFolder2, "firewall.txt"), "firewall port", Encoding.UTF8);
        var service = CreateService();

        var result = await service.BuildManualIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "HelixQAC",
                ManualFolders = [manualFolder1, manualFolder2],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal(2, result.IndexedFileCount);
        var document = await ReadManualIndexAsync(result.IndexFilePath);
        Assert.Contains(document.Manuals, manual => manual.FileName == "license.md");
        Assert.Contains(document.Manuals, manual => manual.FileName == "firewall.txt");
    }

    [Fact]
    public async Task BuildCaseIndexAsync_WritesCaseIndexUnderProductFolder()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseBuilder = new WritingCaseIndexBuilder();
        var service = new ProductScopedIndexService(caseBuilder, new AiManualIndexBuilder());

        var result = await service.BuildCaseIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Checkmarx",
                CloseFolder = sourceFolder,
            },
            aiIndexFolder);

        var expectedProductFolder = Path.Combine(aiIndexFolder, "products", "Checkmarx");
        Assert.Equal(Path.Combine(expectedProductFolder, AiCaseIndexBuilder.IndexFileName), result.IndexFilePath);
        Assert.True(File.Exists(result.IndexFilePath));
        Assert.Equal(sourceFolder, caseBuilder.LastSourceFolder);
    }

    [Fact]
    public void GetProductIndexFolder_SanitizesInvalidProductName()
    {
        using var temp = new TempDirectory();
        var service = CreateService();

        var folder = service.GetProductIndexFolder(temp.Path, "Product/Name:Beta?");

        Assert.StartsWith(Path.Combine(temp.Path, "products"), folder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('/', Path.GetFileName(folder));
        Assert.DoesNotContain(':', Path.GetFileName(folder));
        Assert.DoesNotContain('?', Path.GetFileName(folder));
    }

    [Fact]
    public async Task BuildManualIndexAsync_DoesNotModifySourceManualFiles()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        var manualPath = Path.Combine(manualFolder, "manual.txt");
        await File.WriteAllTextAsync(manualPath, "original manual text", Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(manualPath, expectedLastWriteTime);
        var service = CreateService();

        _ = await service.BuildManualIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Klocwork",
                ManualFolders = [manualFolder],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal("original manual text", await File.ReadAllTextAsync(manualPath, Encoding.UTF8));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(manualPath));
    }

    [Fact]
    public async Task InspectKnowledgeAsync_DetectsExistingIndexWithoutRebuilding()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var product = new ProductKnowledgeSettings { ProductName = "Checkmarx" };
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        Directory.CreateDirectory(productFolder);
        await using (var stream = File.Create(Path.Combine(productFolder, AiManualIndexBuilder.IndexFileName)))
        {
            await JsonSerializer.SerializeAsync(stream, new AiManualIndexDocument
            {
                BuiltAt = DateTimeOffset.Now,
                Manuals = [new AiIndexedManual { Id = "manual-1", FilePath = "manual.txt", FileName = "manual.txt", Text = "content" }],
            });
        }
        await using (var stream = File.Create(Path.Combine(productFolder, KnowledgeManifest.FileName)))
        {
            await JsonSerializer.SerializeAsync(stream, new KnowledgeManifest
            {
                ProductName = product.ProductName,
                IndexedAt = DateTimeOffset.Now,
                LastSuccessfulUpdate = DateTimeOffset.Now,
                Sources =
                [
                    new KnowledgeManifestSource
                    {
                        SourcePathOrUrl = "manuals",
                        SourceType = "Manual",
                        IndexedAt = DateTimeOffset.Now,
                        DocumentCount = 1,
                        ChunkCount = 1,
                    },
                ],
            });
        }

        var status = await CreateService().InspectKnowledgeAsync(product, aiIndexFolder);

        Assert.True(status.UsedExistingIndex);
        Assert.Equal(KnowledgeStatuses.Ready, status.Status);
        Assert.Equal(1, status.ManualDocumentCount);
    }

    [Fact]
    public async Task UpdateKnowledgeAsync_RecentUnchangedOfficialUrlsAreNotFetchedAgain()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        const string url = "https://docs.example.test/release";
        var product = new ProductKnowledgeSettings
        {
            ProductName = "Checkmarx",
            DocumentUrls = [url],
        };
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        Directory.CreateDirectory(productFolder);
        var now = DateTimeOffset.Now;
        await using (var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName)))
        {
            await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
            {
                ProductName = product.ProductName,
                BuiltAt = now,
                SourceUrls = [url],
                Documents = [],
            });
        }

        await using (var stream = File.Create(Path.Combine(productFolder, KnowledgeManifest.FileName)))
        {
            await JsonSerializer.SerializeAsync(stream, new KnowledgeManifest
            {
                ProductName = product.ProductName,
                IndexedAt = now,
                LastSuccessfulUpdate = now,
                Sources =
                [
                    new KnowledgeManifestSource
                    {
                        SourcePathOrUrl = url,
                        SourceType = "OfficialDoc",
                        IndexedAt = now,
                    },
                ],
            });
        }

        var officialBuilder = new CountingOfficialDocumentIndexBuilder();
        var service = new ProductScopedIndexService(new WritingCaseIndexBuilder(), new AiManualIndexBuilder(), officialBuilder);
        _ = await service.UpdateKnowledgeAsync(product, aiIndexFolder);

        Assert.Equal(0, officialBuilder.BuildCount);
    }

    [Fact]
    public async Task UpdateKnowledgeWithEmbeddingsAsync_EmbeddingFailureReportsWarningAndKeepsManualIndex()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "guide.md"), "# QAC analysis\nRun the analysis.", Encoding.UTF8);
        var product = new ProductKnowledgeSettings
        {
            ProductName = "HelixQAC",
            ManualFolders = [manualFolder],
        };
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var service = new ProductScopedIndexService(
            new WritingCaseIndexBuilder(), new AiManualIndexBuilder(),
            embeddingClient: new FailingEmbeddingClient(),
            embeddingModelDigestResolver: new FixedDigestResolver("sha256:test"));

        var result = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, false,
            "nomic-embed-text", "http://localhost:11434");
        var productFolder = service.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        await using var manifestStream = File.OpenRead(Path.Combine(productFolder, KnowledgeManifest.FileName));
        var manifest = await JsonSerializer.DeserializeAsync<KnowledgeManifest>(manifestStream);
        var reopenedStatus = await service.InspectKnowledgeAsync(product, aiIndexFolder);

        Assert.Equal(KnowledgeStatuses.Warning, result.Status.Status);
        Assert.False(result.Embeddings?.IsSuccess);
        Assert.Equal("Warning", manifest?.LastUpdateResult);
        Assert.Equal(KnowledgeStatuses.Warning, reopenedStatus.Status);
        Assert.True(File.Exists(Path.Combine(productFolder, AiManualIndexBuilder.IndexFileName)));
        Assert.False(File.Exists(Path.Combine(productFolder, EmbeddingIndexDocument.FileName)));
    }

    [Fact]
    public async Task UpdateKnowledgeWithEmbeddingsAsync_UnknownDigestDoesNotCallEmbeddingClient()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "guide.md"), "# QAC analysis", Encoding.UTF8);
        var product = new ProductKnowledgeSettings
        {
            ProductName = "HelixQAC",
            ManualFolders = [manualFolder],
        };
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var client = new FailingEmbeddingClient();
        var service = new ProductScopedIndexService(
            new WritingCaseIndexBuilder(), new AiManualIndexBuilder(),
            embeddingClient: client,
            embeddingModelDigestResolver: new FixedDigestResolver(null));

        var result = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, false,
            "nomic-embed-text", "http://localhost:11434");

        Assert.Equal(KnowledgeStatuses.Warning, result.Status.Status);
        Assert.False(result.Embeddings?.IsSuccess);
        Assert.Contains("digest", result.Embeddings?.Warning);
        Assert.Equal(0, client.CallCount);
        Assert.False(File.Exists(result.Embeddings?.IndexFilePath));
    }

    [Fact]
    public async Task UpdateKnowledgeWithEmbeddingsAsync_DigestControlsReuseAndUnknownDigestPreservesIndex()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "guide.md"), "# QAC analysis", Encoding.UTF8);
        var product = new ProductKnowledgeSettings
        {
            ProductName = "HelixQAC",
            ManualFolders = [manualFolder],
        };
        var client = new RecordingEmbeddingClient();
        var resolver = new MutableDigestResolver { Digest = "sha256:first" };
        var service = new ProductScopedIndexService(
            new WritingCaseIndexBuilder(), new AiManualIndexBuilder(),
            embeddingClient: client,
            embeddingModelDigestResolver: resolver);
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");

        var first = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, false,
            "nomic-embed-text", "http://localhost:11434");
        var same = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, false,
            "nomic-embed-text", "http://localhost:11434");
        resolver.Digest = "sha256:second";
        var blocked = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, false,
            "nomic-embed-text", "http://localhost:11434");
        var changed = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, true,
            "nomic-embed-text", "http://localhost:11434");
        var indexPath = changed.Embeddings?.IndexFilePath ?? throw new InvalidOperationException();
        var index = await EmbeddingIndexUpdater.LoadAsync(indexPath);
        var previousBytes = await File.ReadAllBytesAsync(indexPath);
        resolver.Digest = null;
        var unknown = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, false,
            "nomic-embed-text", "http://localhost:11434");

        Assert.True(first.Embeddings?.IsSuccess);
        Assert.Equal(1, same.Embeddings?.UnchangedCount);
        Assert.Equal(KnowledgeStatuses.Warning, blocked.Status.Status);
        Assert.Equal("NeedsRebuild", blocked.Embeddings?.Status);
        Assert.True(changed.Embeddings?.IsSuccess, changed.Embeddings?.Warning);
        Assert.Equal(0, changed.Embeddings?.UnchangedCount);
        Assert.Equal(2, client.CallCount);
        Assert.Equal("sha256:second", index?.EmbeddingModelDigest);
        Assert.Equal(KnowledgeStatuses.Warning, unknown.Status.Status);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(indexPath));
    }

    [Fact]
    public async Task UpdateKnowledgeWithEmbeddingsAsync_LegacyIndexDoesNotAutoRebuild()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "guide.md"), "# QAC analysis", Encoding.UTF8);
        var product = new ProductKnowledgeSettings
        {
            ProductName = "HelixQAC",
            ManualFolders = [manualFolder],
        };
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var client = new RecordingEmbeddingClient();
        var service = new ProductScopedIndexService(
            new WritingCaseIndexBuilder(), new AiManualIndexBuilder(),
            embeddingClient: client,
            embeddingModelDigestResolver: new FixedDigestResolver("sha256:current"));
        _ = await service.BuildManualIndexAsync(product, aiIndexFolder);
        var productFolder = service.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var updater = new EmbeddingIndexUpdater(client);
        var legacy = await updater.UpdateAsync(
            product.ProductName, productFolder, "http://localhost:11434", "nomic-embed-text");
        var indexPath = legacy.IndexFilePath;
        var previousBytes = await File.ReadAllBytesAsync(indexPath);

        var result = await service.UpdateKnowledgeWithEmbeddingsAsync(
            product, aiIndexFolder, KnowledgeUpdateScope.Manuals, false,
            "nomic-embed-text", "http://localhost:11434");

        Assert.True(legacy.IsSuccess);
        Assert.Equal(1, client.CallCount);
        Assert.Equal("NeedsRebuild", result.Embeddings?.Status);
        Assert.Equal(KnowledgeStatuses.Warning, result.Status.Status);
        Assert.Contains("明示的な再構築", result.Status.Message);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(indexPath));
    }

    private static ProductScopedIndexService CreateService()
    {
        return new ProductScopedIndexService(new WritingCaseIndexBuilder(), new AiManualIndexBuilder());
    }

    private sealed class FailingEmbeddingClient : IOllamaEmbeddingClient
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<IReadOnlyList<float>>> EmbedAsync(
            string endpoint,
            string model,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new HttpRequestException("embedding unavailable");
        }
    }

    private sealed class FixedDigestResolver(string? digest) : IEmbeddingModelDigestResolver
    {
        public Task<string?> ResolveAsync(
            string endpoint,
            string model,
            CancellationToken cancellationToken = default) => Task.FromResult(digest);
    }

    private sealed class MutableDigestResolver : IEmbeddingModelDigestResolver
    {
        public string? Digest { get; set; }

        public Task<string?> ResolveAsync(
            string endpoint,
            string model,
            CancellationToken cancellationToken = default) => Task.FromResult(Digest);
    }

    private sealed class RecordingEmbeddingClient : IOllamaEmbeddingClient
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<IReadOnlyList<float>>> EmbedAsync(
            string endpoint,
            string model,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            IReadOnlyList<IReadOnlyList<float>> vectors = inputs
                .Select(static _ => (IReadOnlyList<float>)new float[] { 1, 0 })
                .ToList();
            return Task.FromResult(vectors);
        }
    }

    private static async Task<AiManualIndexDocument> ReadManualIndexAsync(string indexFilePath)
    {
        await using var stream = File.OpenRead(indexFilePath);
        return await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream)
            ?? throw new InvalidOperationException("Manual index JSON could not be deserialized.");
    }

    private sealed class WritingCaseIndexBuilder : IAiCaseIndexBuilder
    {
        public string? LastSourceFolder { get; private set; }

        public async Task<AiCaseIndexBuildResult> BuildAsync(
            string sourceFolder,
            string aiIndexFolder,
            CancellationToken cancellationToken = default)
        {
            LastSourceFolder = sourceFolder;
            Directory.CreateDirectory(aiIndexFolder);
            var indexFilePath = Path.Combine(aiIndexFolder, AiCaseIndexBuilder.IndexFileName);
            await using var stream = File.Create(indexFilePath);
            await JsonSerializer.SerializeAsync(
                stream,
                new AiIndexDocument
                {
                    BuiltAt = new DateTimeOffset(2026, 6, 4, 10, 0, 0, TimeSpan.FromHours(9)),
                    SourceFolder = sourceFolder,
                    Notes = [],
                },
                cancellationToken: cancellationToken);

            return new AiCaseIndexBuildResult
            {
                IndexedCaseCount = 0,
                IndexedNoteCount = 0,
                IndexFilePath = indexFilePath,
            };
        }
    }

    private sealed class CountingOfficialDocumentIndexBuilder : IAiOfficialDocumentIndexBuilder
    {
        public int BuildCount { get; private set; }

        public Task<AiOfficialDocumentIndexBuildResult> BuildAsync(
            ProductKnowledgeSettings product,
            string indexFolder,
            CancellationToken cancellationToken = default)
        {
            BuildCount++;
            return Task.FromResult(new AiOfficialDocumentIndexBuildResult { ProductName = product.ProductName });
        }
    }
}
