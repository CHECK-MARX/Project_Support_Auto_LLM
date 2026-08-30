using System.Diagnostics;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;
using Xunit;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase24LiveEvaluationTests
{
    [Fact]
    public async Task Phase24Cases_RunThroughProductionAnswerPath_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE24_LIVE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = FindRepositoryRoot();
        var cases = JsonSerializer.Deserialize<List<Phase24Case>>(
            await File.ReadAllTextAsync(Path.Combine(root, "tools", "rag-lab", "phase24-2-answer-quality-cases.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        Assert.Equal(16, cases.Count);

        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-data", "settings.json");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var indexFolder = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var search = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());
        using var worker = new RustEvidenceSelectorWorkerClient();
        var rows = new List<object>();

        foreach (var item in cases)
        {
            var productName = item.Product.Equals("Validate", StringComparison.OrdinalIgnoreCase)
                ? "HelixQAC"
                : item.Product;
            var product = settings.Products.FirstOrDefault(p => p.ProductName.Equals(productName, StringComparison.OrdinalIgnoreCase));
            var stopwatch = Stopwatch.StartNew();
            if (product is null)
            {
                rows.Add(new { item.Id, item.Product, item.Type, Readiness = AnswerReadiness.InsufficientEvidence, EvidenceCount = 0, ElapsedMs = stopwatch.ElapsedMilliseconds });
                continue;
            }

            var context = new CaseContext { ProductName = product.ProductName };
            var focus = new InquiryFocusExtractor().Extract(item.Question, context, usePhase175QualityControls: true);
            var sources = item.Product.Equals("Klocwork", StringComparison.OrdinalIgnoreCase)
                ? []
                : await search.SearchAllHybridAsync(product, indexFolder, focus, settings.LlmProvider, 36);
            var viewModels = sources.Select((source, index) => new SearchSourceViewModel(source, index < 5)).ToList();
            var selection = SearchSourceSelectionBuilder.Build(viewModels, 3, settings.AutoSelectMinimumScore, false, true,
                new QuestionAwareEvidenceSelectionContext
                {
                    Enabled = true, InquiryText = item.Question, ProductName = product.ProductName,
                    RankingMode = EvidenceRankingModes.Phase16, UsePhase175QualityControls = true,
                    UseCoverageAwareEvidenceSelection = true, CoverageAwareMaxEvidenceItems = 5,
                    MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000), UseRustEvidenceSelector = true,
                    UsePersistentRustEvidenceSelector = true, RustEvidenceSelectorWorkerClient = worker,
                    RustEvidenceSelectorExecutablePath = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE") ??
                        Path.Combine(root, "tools", "rag-selector-rs", "target", "release", "rag-selector-rs.exe"),
                    RustEvidenceSelectorTimeoutMs = 5000
                });
            var request = new AnswerDraftRequest
            {
                Case = context, InquiryText = item.Question, InquiryFocus = focus, Sources = selection.Sources,
                Settings = settings with { MaxEvidenceItems = Math.Max(3, selection.Sources.Count), MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000), UseAnswerQualityGate = true, UseCoverageAwareEvidenceSelection = true, CoverageAwareMaxEvidenceItems = 5 },
                RequestedAt = DateTimeOffset.Now
            };
            var answer = await new AiAnswerService(new PromptBuilder(), new EvidenceBuilder(), new SafetyRedactionService(), new EmptyLlmClient()).GenerateDraftAsync(request);
            rows.Add(new
            {
                item.Id, item.Product, item.Type, TechnicalQuery = focus.FocusText, RagPipelineMode = "CSharp production path",
                RetrievalMode = "Hybrid", EvidenceCount = answer.Evidence.Count,
                Evidence = answer.Evidence.Select(e => new { e.EvidenceRole, e.SourceType, DocumentTitle = e.DocumentTitle ?? e.Title, Page = e.PageNumber, Section = e.SectionTitle }),
                answer.Readiness, FactCount = 0, ClaimCount = answer.Claims.Count,
                UnsupportedClaimCount = answer.Claims.Count(c => c.SupportLevel == ClaimSupportLevels.Unsupported),
                ConflictingClaimCount = answer.Claims.Count(c => c.Conflicting), Commands = Array.Empty<string>(), Versions = Array.Empty<string>(),
                answer.ReferenceAvailable, answer.ReferenceDisplayed, answer.ReferenceMissingFromIndex,
                DeterministicAnswer = answer.CustomerReplyDraft, FinalAnswer = answer.CustomerReplyDraft,
                AnswerGenerationMode = answer.DeterministicAnswerCreated ? "Deterministic" : "LLM", ElapsedMs = stopwatch.ElapsedMilliseconds,
                SelectorEngine = selection.SelectorEngine
            });
        }

        var reportPath = Environment.GetEnvironmentVariable("SCM_PHASE24_LIVE_REPORT") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "phase24-live-evaluation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { DataClassification = "synthetic-anonymous", CaseCount = rows.Count, Cases = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(16, rows.Count);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SupportCaseManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record Phase24Case(string Id, string Product, string Type, string Question);

    private sealed class EmptyLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(PromptMessages messages, LlmProviderSettings settings, bool disableThinking = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmGenerationResult { Content = string.Empty, DoneReason = "empty" });
    }
}
