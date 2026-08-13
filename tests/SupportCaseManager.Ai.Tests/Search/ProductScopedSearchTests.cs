using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
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

    [Fact]
    public async Task SearchAllAsync_ValidateStream_PreservesRelevantSourceTypesAndSuppressesGenericManuals()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var genericManuals = Enumerable.Range(1, 45)
            .Select(index => CreateManual(
                $"toyo-utility-{index:00}",
                "Validateの利用手順です。プロジェクト設定、登録コンポーネント、解析結果のアップロード方法を説明します。"))
            .ToList();
        genericManuals.Add(CreateManual(
            "stream-manual",
            "Validate Streamは開発中のプロジェクトのビルド履歴を追跡します。ストリームの作成と設定手順を説明します。"));
        await WriteManualIndexAsync(aiIndexFolder, "HelixQAC", genericManuals);
        await WriteCaseIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateNote("stream-past-case", "Validateのストリーム機能とStream設定手順を案内した過去案件です。")]);
        await WriteOfficialIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateOfficial(
                "stream-official",
                "Validate Stream",
                "Stream configuration",
                "Validate Streamの機能概要とストリーム設定手順を説明する公式情報です。")]);
        var service = CreateService();
        var focus = new InquiryFocusExtractor().Extract(
            "ValidateのStream機能について教えてください。また、ストリームの設定方法を教えてください。");

        var results = await service.SearchAllAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            focus,
            maxResults: 12);

        Assert.Contains(results, source => source.SourceType == "Manual" && source.SourceId == "stream-manual");
        Assert.Contains(results, source => source.SourceType == "OfficialDoc" && source.SourceId == "stream-official");
        Assert.Contains(results, source => source.SourceType == "PastCaseNote" && source.SourceId == "stream-past-case");
        Assert.DoesNotContain(results.Take(3), source => source.SourceId.StartsWith("toyo-utility", StringComparison.Ordinal));
        Assert.All(
            results.Where(source => source.SourceId.StartsWith("toyo-utility", StringComparison.Ordinal)),
            source => Assert.Contains("feature=missing", source.ScoreBreakdown, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAllAsync_StreamAndJapaneseAlias_ReturnSameFeatureSources()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [
                CreateManual("english-stream", "Validate Stream configuration procedure."),
                CreateManual("japanese-stream", "Validate ストリーム 設定手順。"),
            ]);
        var service = CreateService();

        var english = await service.SearchAllAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            new InquiryFocus { FocusText = "Validate Stream configuration" },
            maxResults: 8);
        var japanese = await service.SearchAllAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            new InquiryFocus { FocusText = "Validate ストリーム 設定" },
            maxResults: 8);

        Assert.Equal(
            new[] { "english-stream", "japanese-stream" },
            english.Select(static source => source.SourceId).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { "english-stream", "japanese-stream" },
            japanese.Select(static source => source.SourceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SearchAllAsync_ValidateCli_DoesNotRegressToDifferentFeature()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [
                CreateManual(
                    "validate-cli-manual",
                    "qacli validate buildを実行してQACの解析結果をValidateへアップロードする手順です。"),
                CreateManual(
                    "stream-only-manual",
                    "Validate Streamの機能概要とストリーム設定手順です。"),
            ]);
        await WriteOfficialIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateOfficial(
                "validate-cli-official",
                "qacli validate build",
                "Upload analysis results",
                "qacli validate buildコマンドでQACの解析結果をValidateへアップロードします。")]);
        var service = CreateService();
        var focus = new InquiryFocusExtractor().Extract(
            "QACの解析結果をValidateへアップロードするqacli validate buildの方法を教えてください。");

        var results = await service.SearchAllAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            focus,
            maxResults: 5);

        Assert.Contains(results, source => source.SourceId == "validate-cli-manual");
        Assert.Contains(results, source => source.SourceId == "validate-cli-official");
        Assert.DoesNotContain(results.Take(2), source => source.SourceId == "stream-only-manual");
        Assert.All(
            results.Where(source => source.SourceId == "stream-only-manual"),
            source => Assert.Contains("feature=conflict", source.ScoreBreakdown, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAllAsync_ProjectAnalysis_PrefersOperationMatchedSourcesOverDashboard()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [
                CreateManual(
                    "dashboard-manual",
                    "Dashboard利用手順書。QACプロジェクトの解析結果をアップロードし、Dashboardで表示する手順です。"),
                CreateManual(
                    "analysis-manual",
                    "QACプロジェクトを解析する手順です。プロジェクト設定後に qacli analyze -P . を実行し、解析結果を確認します。"),
            ]);
        await WriteCaseIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateNote(
                "analysis-past-case",
                "QACプロジェクトの解析を実行した過去案件です。qacli analyzeの実行前に設定を確認しました。")]);
        await WriteOfficialIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateOfficial(
                "analysis-official",
                "Analyze a project",
                "Running analysis",
                "Run qacli analyze -P <project-directory> to analyze the QAC project and then check the analysis result.")]);
        var service = CreateService();
        var focus = new InquiryFocusExtractor().Extract(
            "QACで、プロジェクトを解析するための手順を教えてください。");

        var results = await service.SearchAllAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            focus,
            maxResults: 6);

        Assert.Contains(results.Take(3), source => source.SourceId == "analysis-manual");
        Assert.Contains(results.Take(3), source => source.SourceId == "analysis-official");
        Assert.Contains(results.Take(3), source => source.SourceId == "analysis-past-case");
        Assert.DoesNotContain(results.Take(3), source => source.SourceId == "dashboard-manual");
    }

    [Fact]
    public async Task SearchAllAsync_SuppressesExactContentDuplicates()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        const string duplicate = "Validate Streamの機能概要とストリーム設定手順を説明します。同じ内容が別形式の文書にも収録されています。";
        await WriteManualIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateManual("stream-docx", duplicate), CreateManual("stream-pdf", duplicate)]);
        var service = CreateService();

        var results = await service.SearchAllAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            new InquiryFocus { FocusText = "Validate Stream 設定手順" },
            maxResults: 8);

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAllAsync_SameSourceIdAcrossTypes_DoesNotDropEitherType()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteManualIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateManual("shared-id", "Validate Streamの設定を説明するマニュアルです。")]);
        await WriteCaseIndexAsync(
            aiIndexFolder,
            "HelixQAC",
            [CreateNote("shared-id", "Validate ストリームの利用方法を回答した過去案件です。")]);
        var service = CreateService();

        var results = await service.SearchAllAsync(
            CreateProduct("HelixQAC"),
            aiIndexFolder,
            new InquiryFocus { FocusText = "Validate Stream 設定方法" },
            maxResults: 8);

        Assert.Contains(results, source => source.SourceType == "Manual" && source.SourceId == "shared-id");
        Assert.Contains(results, source => source.SourceType == "PastCaseNote" && source.SourceId == "shared-id");
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

    private static AiIndexedOfficialDocument CreateOfficial(
        string id,
        string title,
        string sectionTitle,
        string text)
    {
        return new AiIndexedOfficialDocument
        {
            Id = id,
            ProductName = "HelixQAC",
            Url = $"https://docs.example.test/{id}",
            Title = title,
            SectionTitle = sectionTitle,
            Text = text,
            RetrievedAt = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.FromHours(9)),
            ContentHash = id,
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

    private static async Task WriteOfficialIndexAsync(
        string aiIndexFolder,
        string productName,
        IReadOnlyList<AiIndexedOfficialDocument> documents)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = productName,
            BuiltAt = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.FromHours(9)),
            Documents = documents,
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
