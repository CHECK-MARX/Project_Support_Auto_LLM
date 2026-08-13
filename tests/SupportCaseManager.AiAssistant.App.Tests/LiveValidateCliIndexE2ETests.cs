using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class LiveValidateCliIndexE2ETests
{
    private const string Question =
        "QACで解析した結果をValidateへアップロードする方法を教えてください。GUIでのアップロード方法及びCLIでの方法についても教えてください。";

    [Fact]
    public async Task ActualIndex_KeepsGuiAndCliUploadEvidence()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SCM_RUN_LIVE_VALIDATE_CLI_E2E"),
            "1",
            StringComparison.Ordinal))
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
        var aiIndexFolder = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(localAppData, "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var caseContext = new CaseContext { ProductName = product.ProductName };
        var focus = new InquiryFocusExtractor().Extract(Question, caseContext, usePhase175QualityControls: true);
        var search = new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher());
        var candidates = await search.SearchAllHybridAsync(
            product,
            aiIndexFolder,
            focus,
            settings.LlmProvider,
            maxResults: 36);
        var viewModels = candidates
            .Select((source, index) => new SearchSourceViewModel(source, isSelected: index < 3))
            .ToList();
        var rustExecutable = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE") ?? string.Empty;
        var selection = SearchSourceSelectionBuilder.Build(
            viewModels,
            maxEvidenceItems: 3,
            autoSelectMinimumScore: settings.AutoSelectMinimumScore,
            enableTopNFallback: true,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = Question,
                ProductName = product.ProductName,
                RankingMode = EvidenceRankingModes.Phase16,
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 5,
                MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000),
                UseRustEvidenceSelector = File.Exists(rustExecutable),
                RustEvidenceSelectorExecutablePath = rustExecutable,
                RustEvidenceSelectorTimeoutMs = 5000,
            });

        var hasCli = selection.Sources.Any(source =>
            SourceText(source).Contains("qacli validate build", StringComparison.OrdinalIgnoreCase));
        var hasGui = selection.Sources.Any(source =>
            SourceText(source).Contains("解析結果をアップロード", StringComparison.OrdinalIgnoreCase) ||
            (SourceText(source).Contains("ポータル", StringComparison.OrdinalIgnoreCase) &&
             SourceText(source).Contains("Validate", StringComparison.OrdinalIgnoreCase)));

        var reportPath = Environment.GetEnvironmentVariable("SCM_LIVE_VALIDATE_CLI_REPORT");
        await WriteReportAsync(reportPath, new
        {
            Question,
            CandidateCount = candidates.Count,
            SourceTypeCandidateCounts = candidates
                .GroupBy(static source => source.SourceType)
                .ToDictionary(static group => group.Key, static group => group.Count()),
            Candidates = candidates.Select(source => new
            {
                source.SourceId,
                source.SourceType,
                source.Title,
                source.DocumentTitle,
                source.PageNumber,
                source.SectionTitle,
                source.Score,
                source.ScoreBreakdown,
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
                HasGuiProcedure = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source))
                    .Contains(CoverageAnalyzer.GuiUploadProcedure),
                HasCliProcedure = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source))
                    .Contains(CoverageAnalyzer.CliUploadProcedure),
            }),
            Evidence = selection.Sources.Select(source => new
            {
                source.SourceId,
                source.SourceType,
                source.Title,
                source.DocumentTitle,
                source.PageNumber,
                source.SectionTitle,
                source.SupportNumber,
                source.Score,
                source.ScoreBreakdown,
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
            }),
            selection.RequiredCoverage,
            selection.FinalCoverage,
            selection.SelectorEngine,
            selection.RustSelectorFallbackReason,
            HasGuiEvidence = hasGui,
            HasCliEvidence = hasCli,
        });

        Assert.InRange(selection.Sources.Count, 2, 5);
        Assert.True(hasCli, "qacli validate build evidence was not selected.");
        Assert.True(hasGui, "Validate GUI upload evidence was not selected.");
        Assert.DoesNotContain(selection.Sources, source =>
            source.Title.Contains("Dashboard", StringComparison.OrdinalIgnoreCase));
    }

    private static string SourceText(SearchSource source) =>
        string.Join('\n', source.Title, source.SectionTitle, source.Text);

    private static async Task WriteReportAsync(string? reportPath, object report)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
