using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Search;

namespace SupportCaseManager.Ai.Tests.Search;

/// <summary>
/// Opt-in, non-persisting acceptance check against the current local Checkmarx index.
/// It intentionally logs no source text, paths, titles, or customer identifiers.
/// </summary>
public sealed class Phase22CheckmarxLiveIndexE2ETests
{
    private const string Question =
        "Microsoft SQL ServerのストアドプロシージャはCheckmarx SASTの解析対象でしょうか。PL/SQLとの違いも教えてください。";

    [Fact]
    public async Task ActualIndex_UsesTechnicalQueryAndPreservesLegacyAndHybridV2Fallback()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE22_CHECKMARX_E2E"), "1", StringComparison.Ordinal))
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
            string.Equals(item.ProductName, "Checkmarx", StringComparison.OrdinalIgnoreCase));
        var indexPath = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(localAppData, "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var focus = new InquiryFocusExtractor().Extract(
            Question,
            new CaseContext { ProductName = product.ProductName },
            usePhase175QualityControls: true);

        Assert.Contains("Microsoft SQL Server", focus.TechnicalQuery.Technology);
        Assert.Contains("T-SQL", focus.TechnicalQuery.Language);
        Assert.Contains("PL/SQL", focus.TechnicalQuery.Language);
        Assert.Contains("Stored Procedure", focus.TechnicalQuery.Object);
        Assert.DoesNotContain("@", focus.TechnicalQuery.CoreQuestion, StringComparison.Ordinal);

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
        Assert.Contains(legacy, source =>
            string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hybridV2, source =>
            string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase));
    }
}
