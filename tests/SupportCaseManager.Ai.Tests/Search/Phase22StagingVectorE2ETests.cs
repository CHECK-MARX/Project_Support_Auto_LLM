using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Search;

namespace SupportCaseManager.Ai.Tests.Search;

/// <summary>
/// Opt-in, read-only source-index test. It writes only a disposable local
/// staging vector index and deliberately emits no evidence text or paths.
/// </summary>
public sealed class Phase22StagingVectorE2ETests
{
    [Fact]
    public async Task ActualIndexes_BuildStagingVectors_AndRunHybridForFourAcceptanceCases()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE22_STAGING_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var settings = await LoadSettingsAsync();
        Assert.Equal("nomic-embed-text", settings.LlmProvider.EmbeddingModel, ignoreCase: true);
        var indexRoot = ResolveIndexRoot(settings);
        var stagingRoot = Environment.GetEnvironmentVariable("SCM_PHASE22_STAGING_INDEX_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-index-staging", "phase22-nomic");
        var builder = new EmbeddingIndexStagingBuilder();
        var checkmarx = Product(settings, "Checkmarx");
        var helixQac = Product(settings, "HelixQAC");

        foreach (var product in new[] { checkmarx, helixQac })
        {
            var sourceFolder = ProductIndexPathResolver.GetProductIndexFolder(indexRoot, product.ProductName);
            var stagingFolder = Path.Combine(stagingRoot, product.ProductName);
            var stagingIndexPath = Path.Combine(stagingFolder, EmbeddingIndexDocument.FileName);
            var reuseStaging = string.Equals(
                Environment.GetEnvironmentVariable("SCM_PHASE22_REUSE_STAGING"),
                "1",
                StringComparison.Ordinal);
            var built = reuseStaging && File.Exists(stagingIndexPath)
                ? null
                : await builder.BuildAsync(
                    product.ProductName,
                    sourceFolder,
                    stagingRoot,
                    settings.LlmProvider.Endpoint,
                    settings.LlmProvider.EmbeddingModel!);
            var validation = await EmbeddingIndexUpdater.ValidateAsync(
                stagingIndexPath,
                product.ProductName,
                sourceFolder,
                settings.LlmProvider.EmbeddingModel!);

            Assert.True(built?.IsSuccess ?? true, built?.Warning);
            Assert.True(validation.IsValid, validation.Message);
            var index = await EmbeddingIndexUpdater.LoadAsync(stagingIndexPath);
            Assert.NotNull(index);
            Assert.False(string.IsNullOrWhiteSpace(index.EmbeddingModelDigest));
            Assert.Equal("cosine", index.DistanceMetric, ignoreCase: true);
            Assert.All(index.Entries, entry => Assert.True(entry.EmbeddingInputSanitized));
        }

        var search = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());
        await AssertHybridAsync(search, settings, indexRoot, stagingRoot, checkmarx,
            "Microsoft SQL ServerのストアドプロシージャはCheckmarx SASTの解析対象でしょうか。PL/SQLとの違いも教えてください。",
            "OfficialDoc",
            assertTechnicalQuery: focus =>
            {
                Assert.Contains("Microsoft SQL Server", focus.TechnicalQuery.Technology);
                Assert.Contains("T-SQL", focus.TechnicalQuery.Language);
                Assert.Contains("PL/SQL", focus.TechnicalQuery.Language);
                Assert.Contains("Stored Procedure", focus.TechnicalQuery.Object);
            });
        await AssertHybridAsync(search, settings, indexRoot, stagingRoot, helixQac,
            "QACでプロジェクトを解析するまでの手順と、CCT自動生成が必要になる条件を教えてください。",
            "Manual");
        await AssertHybridAsync(search, settings, indexRoot, stagingRoot, helixQac,
            "ValidateのStream機能について、概要と設定方法を教えてください。",
            "OfficialDoc",
            assertResults: results => Assert.DoesNotContain(results.Take(3), source => ContainsForbiddenStreamTopic(source.Title, source.Text)));
        await AssertHybridAsync(search, settings, indexRoot, stagingRoot, helixQac,
            "QAC解析結果をValidateへアップロードするCLI手順を教えてください。",
            "Manual");
    }

    private static async Task AssertHybridAsync(
        ProductScopedSearchService search,
        AiAssistantSettings settings,
        string indexRoot,
        string stagingRoot,
        ProductKnowledgeSettings product,
        string question,
        string expectedSourceType,
        Action<InquiryFocus>? assertTechnicalQuery = null,
        Action<IReadOnlyList<SearchSource>>? assertResults = null)
    {
        var focus = new InquiryFocusExtractor().Extract(
            question,
            new CaseContext { ProductName = product.ProductName },
            usePhase175QualityControls: true);
        assertTechnicalQuery?.Invoke(focus);
        var results = await search.SearchAllHybridAsync(
            product,
            indexRoot,
            focus,
            settings.LlmProvider,
            maxResults: 10,
            ragPipelineMode: RagPipelineModes.HybridV2,
            embeddingIndexFolderOverride: Path.Combine(stagingRoot, product.ProductName));

        Assert.NotEmpty(results);
        Assert.All(results, source => Assert.Contains("RetrievalMode=Hybrid", source.ScoreBreakdown, StringComparison.Ordinal));
        Assert.All(results, source => Assert.True(source.SemanticScore.HasValue));
        Assert.Contains(results, source => string.Equals(source.SourceType, expectedSourceType, StringComparison.OrdinalIgnoreCase));
        assertResults?.Invoke(results);
    }

    private static bool ContainsForbiddenStreamTopic(string title, string text)
    {
        var value = $"{title} {text}";
        return new[] { "License", "Dashboard", "IDE", "Backup", "Toyo Utility" }
            .Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<AiAssistantSettings> LoadSettingsAsync()
    {
        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-data", "settings.json");
        return JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("AI settings could not be loaded.");
    }

    private static ProductKnowledgeSettings Product(AiAssistantSettings settings, string productName) => settings.Products.First(product =>
        string.Equals(product.ProductName, productName, StringComparison.OrdinalIgnoreCase));

    private static string ResolveIndexRoot(AiAssistantSettings settings) => string.IsNullOrWhiteSpace(settings.AiIndexFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-index")
        : settings.AiIndexFolder;
}
