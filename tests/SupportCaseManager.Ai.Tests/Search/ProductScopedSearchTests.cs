using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class ProductScopedSearchTests
{
    [Fact]
    public async Task SearchManualsAsync_SearchesOnlySelectedProductManualIndex()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(aiIndexFolder, "HelixQAC", [CreateManual("helix-manual", "license server port")]);
        await WriteManualIndexAsync(aiIndexFolder, "Checkmarx", [CreateManual("checkmarx-manual", "unrelated text")]);
        var service = CreateService();

        var results = await service.SearchManualsAsync(CreateProduct("HelixQAC"), aiIndexFolder, "license", maxResults: 8);

        var result = Assert.Single(results);
        Assert.Equal("helix-manual", result.SourceId);
        Assert.Equal("Manual", result.SourceType);
        Assert.Equal("HelixQAC", result.ProductName);
    }

    [Fact]
    public async Task SearchManualsAsync_DoesNotMixOtherProductManuals()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(aiIndexFolder, "HelixQAC", [CreateManual("helix-manual", "license server port")]);
        await WriteManualIndexAsync(aiIndexFolder, "Checkmarx", [CreateManual("checkmarx-manual", "unrelated text")]);
        var service = CreateService();

        var results = await service.SearchManualsAsync(CreateProduct("Checkmarx"), aiIndexFolder, "license", maxResults: 8);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchPastCasesAsync_SearchesOnlySelectedProductCaseIndex()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteCaseIndexAsync(aiIndexFolder, "HelixQAC", [CreateNote("helix-case", "startup crash fixed by license configuration")]);
        await WriteCaseIndexAsync(aiIndexFolder, "Checkmarx", [CreateNote("checkmarx-case", "unrelated issue")]);
        var service = CreateService();

        var results = await service.SearchPastCasesAsync(CreateProduct("HelixQAC"), aiIndexFolder, "license", maxResults: 8);

        var result = Assert.Single(results);
        Assert.Equal("helix-case", result.SourceId);
        Assert.Equal("PastCaseNote", result.SourceType);
        Assert.Equal("HelixQAC", result.ProductName);
    }

    [Fact]
    public async Task SearchResults_CanBePassedToAnswerDraftRequestSources()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(aiIndexFolder, "HelixQAC", [CreateManual("helix-manual", "license server port")]);
        var service = CreateService();

        var results = await service.SearchManualsAsync(CreateProduct("HelixQAC"), aiIndexFolder, "license", maxResults: 8);
        var request = new AnswerDraftRequest
        {
            Sources = results,
        };

        var source = Assert.Single(request.Sources);
        Assert.Equal("helix-manual", source.SourceId);
        Assert.Equal("HelixQAC", source.ProductName);
    }

    [Fact]
    public async Task SearchAllHybridAsync_UsesEmbeddingSimilarityToRerankKeywordMatches()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(aiIndexFolder, "HelixQAC",
        [
            CreateManual("manual-a", "license guidance"),
            CreateManual("manual-b", "license guidance"),
        ]);
        await WriteEmbeddingIndexAsync(aiIndexFolder, "HelixQAC");
        var service = new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher(),
            embeddingClient: new StaticEmbeddingClient());

        var results = await service.SearchAllHybridAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            new InquiryFocus { FocusText = "license" },
            new LlmProviderSettings
            {
                Endpoint = "http://localhost:11434",
                EmbeddingModel = "nomic-embed-text",
            },
            maxResults: 2);

        Assert.Equal("manual-b", results[0].SourceId);
        Assert.Contains("Hybrid embedding", results[0].ScoreBreakdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAllHybridAsync_WhenEmbeddingFails_ReturnsKeywordResults()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(aiIndexFolder, "HelixQAC", [CreateManual("manual-a", "license guidance")]);
        await WriteEmbeddingIndexAsync(aiIndexFolder, "HelixQAC");
        var service = new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher(),
            embeddingClient: new ThrowingEmbeddingClient());

        var results = await service.SearchAllHybridAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            new InquiryFocus { FocusText = "license" },
            new LlmProviderSettings
            {
                Endpoint = "http://localhost:11434",
                EmbeddingModel = "nomic-embed-text",
            });

        Assert.Single(results);
        Assert.Equal("manual-a", results[0].SourceId);
        Assert.Contains("Hybrid local", results[0].ScoreBreakdown, StringComparison.Ordinal);
    }

    private static ProductScopedSearchService CreateService()
    {
        return new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());
    }

    private static ProductKnowledgeSettings CreateProduct(string productName)
    {
        return new ProductKnowledgeSettings
        {
            ProductName = productName,
            IsEnabled = true,
        };
    }

    private static AiIndexedManual CreateManual(string id, string text)
    {
        return new AiIndexedManual
        {
            Id = id,
            FilePath = $@"D:\Manuals\{id}.md",
            FileName = $"{id}.md",
            Title = id,
            DocumentType = "Markdown",
            SectionTitle = "Section",
            Text = text,
        };
    }

    private static AiIndexedNote CreateNote(string id, string text)
    {
        return new AiIndexedNote
        {
            Id = id,
            CaseFolderPath = $@"D:\Closed\{id}",
            CaseFolderName = id,
            SupportNumber = "00001234",
            NoteKind = "Note",
            NoteFilePath = $@"D:\Closed\{id}\note.txt",
            Title = id,
            Text = text,
        };
    }

    private static async Task WriteManualIndexAsync(
        string aiIndexFolder,
        string productName,
        IReadOnlyList<AiIndexedManual> manuals)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiManualIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiManualIndexDocument
        {
            BuiltAt = new DateTimeOffset(2026, 6, 4, 10, 0, 0, TimeSpan.FromHours(9)),
            SourceFolder = @"D:\Manuals",
            Manuals = manuals,
        });
    }

    private static async Task WriteCaseIndexAsync(
        string aiIndexFolder,
        string productName,
        IReadOnlyList<AiIndexedNote> notes)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiCaseIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiIndexDocument
        {
            BuiltAt = new DateTimeOffset(2026, 6, 4, 10, 0, 0, TimeSpan.FromHours(9)),
            SourceFolder = @"D:\Closed",
            Notes = notes,
        });
    }

    private static async Task WriteEmbeddingIndexAsync(string aiIndexFolder, string productName)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, EmbeddingIndexDocument.FileName));
        await JsonSerializer.SerializeAsync(stream, new EmbeddingIndexDocument
        {
            ProductName = productName,
            EmbeddingModel = "nomic-embed-text",
            BuiltAt = DateTimeOffset.Now,
            Entries =
            [
                new EmbeddingIndexEntry
                {
                    SourceId = "manual-a",
                    SourceType = "Manual",
                    ProductName = productName,
                    ContentHash = "a",
                    Vector = [1, 0],
                },
                new EmbeddingIndexEntry
                {
                    SourceId = "manual-b",
                    SourceType = "Manual",
                    ProductName = productName,
                    ContentHash = "b",
                    Vector = [0, 1],
                },
            ],
        });
    }

    private sealed class StaticEmbeddingClient : IOllamaEmbeddingClient
    {
        public Task<IReadOnlyList<IReadOnlyList<float>>> EmbedAsync(
            string endpoint,
            string model,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<IReadOnlyList<float>> vectors = inputs
                .Select(static _ => (IReadOnlyList<float>)new float[] { 0, 1 })
                .ToList();
            return Task.FromResult(vectors);
        }
    }

    private sealed class ThrowingEmbeddingClient : IOllamaEmbeddingClient
    {
        public Task<IReadOnlyList<IReadOnlyList<float>>> EmbedAsync(
            string endpoint,
            string model,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("embedding unavailable");
        }
    }
}
