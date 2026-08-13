using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class LiveAnalysisIndexE2ETests
{
    private const string Question =
        "QACで、プロジェクトを解析するための手順を教えてください。";

    [Fact]
    public async Task ActualIndex_SelectsThreeAnalysisEvidenceAndBuildsDirectFallbackAnswer()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SCM_RUN_LIVE_ANALYSIS_E2E"),
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
                CoverageAwareMaxEvidenceItems = 3,
                MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000),
            });

        var reportPath = Environment.GetEnvironmentVariable("SCM_LIVE_ANALYSIS_REPORT");
        await WriteReportAsync(reportPath, new
        {
            Question,
            SettingsPath = settingsPath,
            IndexPath = aiIndexFolder,
            CandidateCount = candidates.Count,
            Candidates = candidates.Select(source => new
            {
                source.SourceId,
                source.SourceType,
                source.Title,
                source.SupportNumber,
                source.Score,
                source.ScoreBreakdown,
                AnalysisOperationMatch = ContainsAnalysisOperation(source),
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
            }),
            Evidence = selection.Sources.Select(source => new
            {
                source.SourceId,
                source.SourceType,
                source.Title,
                source.SupportNumber,
                source.Score,
                source.ScoreBreakdown,
                SelectionReason = BuildSelectionReason(source),
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
            }),
            selection.RequiredCoverage,
            selection.FinalCoverage,
            AnswerGeneration = "not started",
        });

        Assert.Equal(3, selection.Sources.Count);
        Assert.All(selection.Sources, source => Assert.True(ContainsAnalysisOperation(source)));
        Assert.Contains(selection.Sources, source =>
            string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase) &&
            SourceText(source).Contains("qacli analyze", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selection.Sources, source =>
            string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selection.Sources, source =>
            source.SourceType.Equals("PastCaseNote", StringComparison.OrdinalIgnoreCase) ||
            source.SourceType.Equals("PastAnswer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selection.Sources, source =>
            source.Title.Contains("Dashboard", StringComparison.OrdinalIgnoreCase));

        var request = new AnswerDraftRequest
        {
            Case = caseContext,
            InquiryText = Question,
            InquiryFocus = focus,
            Sources = selection.Sources,
            Settings = settings with
            {
                MaxEvidenceItems = 3,
                MaxPromptChars = Math.Max(settings.MaxPromptChars, 6000),
                UseAnswerQualityGate = false,
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 3,
            },
            RequestedAt = DateTimeOffset.Now,
        };
        var answerService = new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new TruncatedJsonLlmClient());
        var answer = await answerService.GenerateDraftAsync(request);

        Assert.Contains("[会社名]", answer.CustomerReplyDraft);
        Assert.Contains("[お客様名] 様", answer.CustomerReplyDraft);
        Assert.Contains("概要", answer.CustomerReplyDraft);
        Assert.Contains("手順", answer.CustomerReplyDraft);
        Assert.Contains("注意点", answer.CustomerReplyDraft);
        Assert.Contains("qacli analyze", answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOYO", answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("東陽テクニカ", answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);

        await WriteReportAsync(reportPath, new
        {
            Question,
            SettingsPath = settingsPath,
            IndexPath = aiIndexFolder,
            CandidateCount = candidates.Count,
            Candidates = candidates.Select(source => new
            {
                source.SourceId,
                source.SourceType,
                source.Title,
                source.SupportNumber,
                source.Score,
                source.ScoreBreakdown,
                AnalysisOperationMatch = ContainsAnalysisOperation(source),
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
            }),
            SourceTypeCandidateCounts = candidates
                .GroupBy(static source => source.SourceType)
                .ToDictionary(static group => group.Key, static group => group.Count()),
            Evidence = selection.Sources.Select(source => new
            {
                source.SourceId,
                source.SourceType,
                source.Title,
                source.SupportNumber,
                source.Url,
                source.Score,
                source.ScoreBreakdown,
                SelectionReason = BuildSelectionReason(source),
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
            }),
            selection.RequiredCoverage,
            selection.FinalCoverage,
            AnswerGeneration = "Existing grounded fallback after simulated truncated LLM JSON",
            FullAnswer = answer.CustomerReplyDraft,
            answer.Warnings,
        });
    }

    private static bool ContainsAnalysisOperation(SearchSource source)
    {
        var profile = TopicEntityAnalyzer.Extract(
            SourceText(source),
            SupportTopicCatalog.Create("HelixQAC"));
        return profile.Operations.Contains("Analysis", StringComparer.Ordinal);
    }

    private static string BuildSelectionReason(SearchSource source)
    {
        var coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source));
        return $"explicit QAC project-analysis operation match; coverage={string.Join(',', coverage)}; " +
            $"score={source.Score:0.000}; {source.ScoreBreakdown}";
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

    private sealed class TruncatedJsonLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmGenerationResult
            {
                Content = "{\"customerReplyDraft\":\"",
                DoneReason = "length",
            });
    }
}
