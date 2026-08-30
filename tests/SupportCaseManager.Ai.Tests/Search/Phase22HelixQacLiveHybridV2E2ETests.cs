using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Search;

namespace SupportCaseManager.Ai.Tests.Search;

/// <summary>Opt-in, non-persisting Legacy/HybridV2 retrieval comparison on the local HelixQAC index.</summary>
public sealed class Phase22HelixQacLiveHybridV2E2ETests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["analysis-cct", "QACでプロジェクトを解析するまでの手順と、CCT自動生成が必要になる条件を教えてください。", "Manual"];
        yield return ["validate-stream", "ValidateのStream機能について、概要と設定方法を教えてください。", "OfficialDoc"];
        yield return ["validate-upload", "QAC解析結果をValidateへアップロードするCLI手順を教えてください。", "Manual"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualIndex_LegacyAndHybridV2ReturnExpectedSourceFamily(
        string _, string question, string sourceType)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE22_HELIX_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(localAppData, "SupportCaseManager", "ai-data", "settings.json");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("AI settings could not be loaded.");
        var product = settings.Products.First(item =>
            string.Equals(item.ProductName, "HelixQAC", StringComparison.OrdinalIgnoreCase));
        var indexPath = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(localAppData, "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var focus = new InquiryFocusExtractor().Extract(
            question,
            new CaseContext { ProductName = product.ProductName },
            usePhase175QualityControls: true);
        var search = new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher());

        var legacy = await search.SearchAllHybridAsync(
            product, indexPath, focus, settings.LlmProvider, maxResults: 36,
            ragPipelineMode: RagPipelineModes.Legacy);
        var hybridV2 = await search.SearchAllHybridAsync(
            product, indexPath, focus, settings.LlmProvider, maxResults: 36,
            ragPipelineMode: RagPipelineModes.HybridV2);

        Assert.NotEmpty(legacy);
        Assert.NotEmpty(hybridV2);
        Assert.Contains(legacy, source => string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hybridV2, source => string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase));
    }
}
