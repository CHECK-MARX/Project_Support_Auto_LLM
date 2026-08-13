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
        var rustExecutable = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE") ?? string.Empty;
        var useRust = File.Exists(rustExecutable);
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
                UseRustEvidenceSelector = useRust,
                RustEvidenceSelectorExecutablePath = rustExecutable,
                RustEvidenceSelectorTimeoutMs = 5000,
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
                source.DocumentTitle,
                source.PageNumber,
                source.SectionTitle,
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
                source.DocumentTitle,
                source.PageNumber,
                source.SectionTitle,
                source.SupportNumber,
                source.Score,
                source.ScoreBreakdown,
                SelectionReason = BuildSelectionReason(source),
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
            }),
            selection.RequiredCoverage,
            selection.FinalCoverage,
            selection.SelectorEngine,
            selection.RustSelectorFallbackReason,
            AnswerGeneration = "not started",
        });

        Assert.InRange(selection.Sources.Count, 3, 5);
        Assert.All(selection.Sources, source => Assert.True(ContainsAnalysisOperation(source)));
        Assert.Contains(selection.Sources, source =>
            string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selection.Sources, source =>
            source.Title.Contains("Dashboard", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selection.Sources, source =>
            source.Title.Contains("Toyo_Utility", StringComparison.OrdinalIgnoreCase) ||
            source.Text.Contains("東陽ユーティリティ", StringComparison.OrdinalIgnoreCase));

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
                CoverageAwareMaxEvidenceItems = 5,
            },
            RequestedAt = DateTimeOffset.Now,
        };
        var fallbackAnswerService = new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new TruncatedJsonLlmClient());
        var fallbackAnswer = await fallbackAnswerService.GenerateDraftAsync(request);
        var successfulAnswerService = new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new StructuredAnalysisJsonLlmClient());
        var successfulAnswer = await successfulAnswerService.GenerateDraftAsync(request);

        AssertAnalysisAnswer(fallbackAnswer.CustomerReplyDraft);
        AssertAnalysisAnswer(successfulAnswer.CustomerReplyDraft);
        Assert.True(
            fallbackAnswer.CustomerReplyDraft.Contains("解析ダイアログ", StringComparison.Ordinal) ||
            fallbackAnswer.CustomerReplyDraft.Contains("［問題］パネル", StringComparison.Ordinal));

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
                source.DocumentTitle,
                source.PageNumber,
                source.SectionTitle,
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
                source.DocumentTitle,
                source.PageNumber,
                source.SectionTitle,
                source.SupportNumber,
                source.Url,
                source.Score,
                source.ScoreBreakdown,
                SelectionReason = BuildSelectionReason(source),
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
            }),
            selection.RequiredCoverage,
            selection.FinalCoverage,
            selection.SelectorEngine,
            selection.RustSelectorFallbackReason,
            selection.RustSelectorParityValidation,
            AnswerGeneration = "Both deterministic successful LLM JSON and grounded fallback after simulated truncated JSON",
            LlmSuccessfulAnswer = successfulAnswer.CustomerReplyDraft,
            LlmFailureFallbackAnswer = fallbackAnswer.CustomerReplyDraft,
            SuccessfulWarnings = successfulAnswer.Warnings,
            FallbackWarnings = fallbackAnswer.Warnings,
        });
    }

    private static void AssertAnalysisAnswer(string answer)
    {
        Assert.Contains("[会社名]", answer);
        Assert.Contains("[お客様名] 様", answer);
        Assert.Contains("【事前準備】", answer);
        Assert.Contains("【GUIでの手順】", answer);
        Assert.Contains("【CLIでの手順】", answer);
        Assert.Contains("【解析結果の確認】", answer);
        Assert.Contains("【注意点】", answer);
        Assert.Contains("【参照先】", answer);
        Assert.Contains("qacli analyze", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOYO", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("東陽テクニカ", answer, StringComparison.OrdinalIgnoreCase);
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

    private sealed class StructuredAnalysisJsonLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmGenerationResult
            {
                Content = JsonSerializer.Serialize(new
                {
                    customerReplyDraft = "【事前準備】\nソースファイルとコンパイラ設定を確認します。\n\n【GUIでの手順】\nQAGUIで［解析(N)］>プロジェクト全体のファイルベース解析を選択します。\n\n【CLIでの手順】\n`qacli analyze -cf -P<directory>` を実行します。\n\n【解析結果の確認】\n解析中ダイアログにプロセスが表示されることを確認します。\n\n【注意点】\n対象バージョンとビルド環境を確認してください。\n\n【参照先】\nPerforce-QAC-Manual",
                    internalMemo = "Selected evidence was used.",
                    needConfirmations = Array.Empty<object>(),
                    evidence = Array.Empty<object>(),
                    confidence = 0.9,
                    warnings = Array.Empty<string>(),
                }),
                DoneReason = "stop",
            });
    }
}
