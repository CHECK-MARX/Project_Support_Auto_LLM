using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class LiveStreamIndexE2ETests
{
    private const string Question =
        "Validateのストリーム機能についてどのような機能かを教えてください。また、設定方法について教えてください。";

    [Fact]
    public async Task ActualIndex_SelectsThreeFeatureMatchedEvidenceAndGeneratesDirectAnswer()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SCM_RUN_LIVE_STREAM_E2E"),
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

        var reportPath = Environment.GetEnvironmentVariable("SCM_LIVE_STREAM_REPORT");
        var candidateReport = candidates.Select(source => new
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
            StreamFeatureMatch = ContainsStreamFeature(source),
        }).ToList();
        var evidenceReport = selection.Sources.Select(source => new
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
            Coverage = CoverageAnalyzer.ObserveForCoverageSelection(
                string.Join('\n', source.Title, source.SectionTitle, source.Text)),
        }).ToList();
        var officialDocDiagnostics = OfficialDocDiagnosticsBuilder.Build(
            product,
            aiIndexFolder,
            focus,
            candidates,
            viewModels.Where(item => item.IsSelected).Select(item => item.Source).ToList(),
            selection.Sources);
        await WriteReportAsync(reportPath, new
        {
            Question,
            CandidateCount = candidates.Count,
            Candidates = candidateReport,
            Evidence = evidenceReport,
            RequiredCoverage = selection.RequiredCoverage,
            FinalCoverage = selection.FinalCoverage,
            selection.SelectorEngine,
            selection.RustSelectorFallbackReason,
            OfficialDocDiagnostics = officialDocDiagnostics,
            Answer = "(generation not started)",
        });

        Assert.InRange(selection.Sources.Count, 3, 5);
        Assert.All(selection.Sources, source => Assert.True(ContainsStreamFeature(source)));
        Assert.Contains(selection.Sources, source => IsPastEvidence(source.SourceType));
        Assert.DoesNotContain(selection.Sources, source =>
            source.Title.Contains("Toyo_Utility", StringComparison.OrdinalIgnoreCase));

        var liveModel = Environment.GetEnvironmentVariable("SCM_LIVE_STREAM_MODEL");
        var effectiveSettings = settings with
        {
            MaxEvidenceItems = 3,
            MaxPromptChars = Math.Max(settings.MaxPromptChars, 6000),
            UseCoverageAwareEvidenceSelection = true,
            CoverageAwareMaxEvidenceItems = 5,
            UsePhase175QualityControls = true,
            DisableThinking = true,
            LlmProvider = settings.LlmProvider with
            {
                ChatModel = liveModel ?? settings.LlmProvider.ChatModel,
                MaxOutputTokens = string.IsNullOrWhiteSpace(liveModel)
                    ? Math.Max(settings.LlmProvider.MaxOutputTokens, 800)
                    : 600,
                ThinkingParameterType = string.IsNullOrWhiteSpace(liveModel)
                    ? settings.LlmProvider.ThinkingParameterType
                    : ThinkingParameterTypes.Boolean,
                ThinkingValue = string.IsNullOrWhiteSpace(liveModel)
                    ? settings.LlmProvider.ThinkingValue
                    : "false",
            },
        };
        var request = new AnswerDraftRequest
        {
            Case = caseContext,
            InquiryText = Question,
            InquiryFocus = focus,
            Sources = selection.Sources,
            Settings = effectiveSettings,
            RequestedAt = DateTimeOffset.Now,
        };
        var useLiveOllama = string.Equals(
            Environment.GetEnvironmentVariable("SCM_RUN_LIVE_OLLAMA"),
            "1",
            StringComparison.Ordinal);
        var answerService = new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            useLiveOllama ? new OllamaClient() : new StructuredStreamJsonLlmClient());
        var answer = await answerService.GenerateDraftAsync(request);

        var report = new
        {
            Question,
            CandidateCount = candidates.Count,
            Candidates = candidateReport,
            Evidence = evidenceReport,
            RequiredCoverage = selection.RequiredCoverage,
            FinalCoverage = selection.FinalCoverage,
            selection.SelectorEngine,
            selection.RustSelectorFallbackReason,
            selection.RustSelectorParityValidation,
            OfficialDocDiagnostics = officialDocDiagnostics,
            Model = effectiveSettings.LlmProvider.ChatModel,
            GenerationMode = useLiveOllama ? "Live Ollama" : "Deterministic valid LLM JSON",
            Answer = answer.CustomerReplyDraft,
            answer.InternalMemo,
            answer.Warnings,
        };
        await WriteReportAsync(reportPath, report);

        Assert.False(string.IsNullOrWhiteSpace(answer.CustomerReplyDraft));
        Assert.Contains("ストリーム", answer.CustomerReplyDraft);
        Assert.Contains("設定", answer.CustomerReplyDraft);
    }

    private static bool ContainsStreamFeature(SearchSource source)
    {
        var text = string.Join('\n', source.Title, source.SectionTitle, source.Text);
        return text.Contains("Stream", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ストリーム", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPastEvidence(string? sourceType) => sourceType is not null &&
        (sourceType.Equals("PastCaseNote", StringComparison.OrdinalIgnoreCase) ||
         sourceType.Equals("PastAnswer", StringComparison.OrdinalIgnoreCase) ||
         sourceType.Equals("ExactPastAnswer", StringComparison.OrdinalIgnoreCase));

    private static string BuildSelectionReason(SearchSource source)
    {
        var text = string.Join('\n', source.Title, source.SectionTitle, source.Text);
        var role = IsPastEvidence(source.SourceType)
            ? "related prior-case supplement"
            : text.Contains("qacli validate", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("--validate-project", StringComparison.OrdinalIgnoreCase)
                ? "configuration coverage"
                : "feature overview coverage";
        return $"explicit Stream feature match; {role}; {source.ScoreBreakdown}";
    }

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

    private sealed class StructuredStreamJsonLlmClient : ILlmClient
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
                    customerReplyDraft = "Validateのストリームは、QACプロジェクトの異なるバージョンを追跡し、特定のストリームへ関連付けて管理する機能です。設定は、選択した根拠に記載されたValidate画面またはqacliの設定手順に従って対象ストリームを作成し、QACプロジェクトを関連付けます。対象バージョンでの画面名と権限はマニュアルで確認してください。",
                    internalMemo = "Selected Stream evidence was used.",
                    needConfirmations = Array.Empty<object>(),
                    evidence = Array.Empty<object>(),
                    confidence = 0.85,
                    warnings = Array.Empty<string>(),
                }),
                DoneReason = "stop",
            });
    }
}
