using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Ranking;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase31StagingAnswerTests
{
    [Fact]
    public async Task Phase31Staging_PreservesCliCommandAndCctSafety_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE31_ACCEPTANCE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            throw new InvalidOperationException("SCM_LIVE_SETTINGS_PATH must point to the isolated staging settings.");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("AI settings could not be loaded.");
        var product = settings.Products.Single(item =>
            item.ProductName.Equals("HelixQAC", StringComparison.OrdinalIgnoreCase));
        var rustExecutable = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE") ?? string.Empty;
        Assert.True(File.Exists(rustExecutable), rustExecutable);

        using var worker = new RustEvidenceSelectorWorkerClient();
        var cli = await RunAsync(
            "QACの解析CLIコマンドとオプションを教えてください。",
            product,
            settings,
            rustExecutable,
            worker);
        var cct = await RunAsync(
            "QACのCCT生成方法から、プロジェクトの解析までの方法について詳細を教えてください",
            product,
            settings,
            rustExecutable,
            worker);
        var provenance = cli.Sources
            .SelectMany(HowToAnswerComposer.ExtractAnalysisCommandProvenance)
            .ToList();
        var complete = provenance
            .Where(item => item.Integrity == HowToAnswerComposer.CliCommandIntegrity.Complete)
            .ToList();

        Assert.NotEmpty(complete);
        Assert.Contains("`qacli analyze -cf -P <directory>`", cli.Answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("qaclianalyze", cli.Answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<directory>1\"R\"", cli.Answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Helix_Generic_C.cct", cct.Answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default.acf", cct.Answer.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("共有いただいた", cct.Answer.CustomerReplyDraft, StringComparison.Ordinal);

        var reportPath = Environment.GetEnvironmentVariable("SCM_PHASE31_ACCEPTANCE_REPORT") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "phase31", "phase31-answer-acceptance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            DataClassification = "synthetic-anonymous-aggregate",
            Cli = new
            {
                cli.Answer.Readiness,
                EvidenceCount = cli.Sources.Count,
                CompleteCommands = complete.Count,
                AmbiguousCommands = provenance.Count(item =>
                    item.Integrity == HowToAnswerComposer.CliCommandIntegrity.Ambiguous),
                RejectedCommands = provenance.Count(item =>
                    item.Integrity is HowToAnswerComposer.CliCommandIntegrity.Incomplete or
                        HowToAnswerComposer.CliCommandIntegrity.Rejected),
                Commands = complete.Select(item => new
                {
                    item.SourceEvidenceId,
                    item.DocumentTitle,
                    item.PageNumber,
                    item.SectionTitle,
                    item.RawCommandText,
                    item.NormalizedCommandText,
                    Integrity = item.Integrity.ToString(),
                }),
            },
            Cct = new
            {
                cct.Answer.Readiness,
                EvidenceCount = cct.Sources.Count,
                HelixGenericCctLeakage = cct.Answer.CustomerReplyDraft.Contains("Helix_Generic_C.cct", StringComparison.OrdinalIgnoreCase),
                DefaultAcfLeakage = cct.Answer.CustomerReplyDraft.Contains("default.acf", StringComparison.OrdinalIgnoreCase),
                SharedPhraseLeakage = cct.Answer.CustomerReplyDraft.Contains("共有いただいた", StringComparison.Ordinal),
            },
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<RunResult> RunAsync(
        string question,
        ProductKnowledgeSettings product,
        AiAssistantSettings settings,
        string rustExecutable,
        RustEvidenceSelectorWorkerClient worker)
    {
        var context = new CaseContext { ProductName = product.ProductName };
        var focus = new InquiryFocusExtractor().Extract(question, context, usePhase175QualityControls: true);
        var search = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());
        var candidates = await search.SearchAllHybridAsync(product, settings.AiIndexFolder, focus, settings.LlmProvider, 36);
        var viewModels = candidates.Select((source, index) => new SearchSourceViewModel(source, index < 5)).ToList();
        var selection = SearchSourceSelectionBuilder.Build(
            viewModels,
            3,
            settings.AutoSelectMinimumScore,
            enableTopNFallback: true,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = question,
                ProductName = product.ProductName,
                RankingMode = EvidenceRankingModes.Phase16,
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 5,
                MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000),
                UseRustEvidenceSelector = true,
                UsePersistentRustEvidenceSelector = true,
                RustEvidenceSelectorWorkerClient = worker,
                RustEvidenceSelectorExecutablePath = rustExecutable,
                RustEvidenceSelectorTimeoutMs = 5000,
            });
        var answer = await new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new EmptyLlmClient()).GenerateDraftAsync(new AnswerDraftRequest
            {
                Case = context,
                InquiryText = question,
                InquiryFocus = focus,
                Sources = selection.Sources,
                Settings = settings with
                {
                    MaxEvidenceItems = Math.Max(3, selection.Sources.Count),
                    MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000),
                    UseAnswerQualityGate = true,
                    UseCoverageAwareEvidenceSelection = true,
                    CoverageAwareMaxEvidenceItems = 5,
                },
                RequestedAt = DateTimeOffset.Now,
            });

        return new RunResult(selection.Sources, answer);
    }

    private sealed record RunResult(IReadOnlyList<SearchSource> Sources, AnswerDraftResult Answer);

    private sealed class EmptyLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmGenerationResult { Content = string.Empty, DoneReason = "empty" });
    }
}
