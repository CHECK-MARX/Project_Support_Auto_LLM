using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;
using Xunit;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase28SingleCaseTraceTests
{
    [Fact]
    public void QacCliOption_IsClassifiedAsProjectAnalysisCommand()
    {
        var profile = TopicEntityAnalyzer.Extract(
            "QACの解析CLIコマンドとオプションを教えてください。",
            SupportTopicCatalog.Create("HelixQAC"));

        Assert.Contains("Analysis", profile.Operations);
        Assert.Contains("Project Analysis", profile.Features);
        Assert.Contains("Command", profile.Intents);
    }

    [Fact]
    public async Task QacCliOption_WritesAnonymousProductionPathTrace_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE28_TRACE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = FindRepositoryRoot();
        var cases = JsonSerializer.Deserialize<List<Phase28Case>>(
            await File.ReadAllTextAsync(Path.Combine(root, "tools", "rag-lab", "phase24-2-answer-quality-cases.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var testCase = cases.Single(item => item.Id == "qac-cli-option");
        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-data", "settings.json");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var indexFolder = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var product = settings.Products.First(item => item.ProductName.Equals("HelixQAC", StringComparison.OrdinalIgnoreCase));
        var focus = new InquiryFocusExtractor().Extract(testCase.Question, new CaseContext { ProductName = product.ProductName }, true);
        var search = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());
        var sources = await search.SearchAllHybridAsync(product, indexFolder, focus, settings.LlmProvider, 36);
        var viewModels = sources.Select((source, index) => new SearchSourceViewModel(source, index < 5)).ToList();
        using var worker = new RustEvidenceSelectorWorkerClient();
        var context = new QuestionAwareEvidenceSelectionContext
        {
            Enabled = true,
            InquiryText = testCase.Question,
            ProductName = product.ProductName,
            RankingMode = EvidenceRankingModes.Phase16,
            UsePhase175QualityControls = true,
            UseCoverageAwareEvidenceSelection = true,
            CoverageAwareMaxEvidenceItems = 5,
            MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000),
            UseRustEvidenceSelector = true,
            UsePersistentRustEvidenceSelector = true,
            RustEvidenceSelectorWorkerClient = worker,
            RustEvidenceSelectorExecutablePath = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE") ??
                Path.Combine(root, "tools", "rag-selector-rs", "target", "release", "rag-selector-rs.exe"),
            RustEvidenceSelectorTimeoutMs = 5000,
        };
        var selection = SearchSourceSelectionBuilder.Build(viewModels, 3, settings.AutoSelectMinimumScore, false, true, context);
        var ranking = QuestionAwareEvidenceRanker.Rank(viewModels, context, 36);
        var rankedById = ranking.Ranked
            .Select((item, rank) =>
            {
                var index = viewModels.FindIndex(candidate => ReferenceEquals(candidate, item.Item));
                return (Id: CandidateId(item.Item, index), Rank: rank + 1, Item: item);
            })
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var selectedIds = selection.Sources.Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);
        var trace = new
        {
            DataClassification = "synthetic-anonymous-trace",
            CaseId = testCase.Id,
            Product = product.ProductName,
            TechnicalQuery = focus.TechnicalQuery,
            Feature = "Project Analysis",
            Operation = "Analysis",
            Intent = "HowTo",
            RequiredCoverage = CoverageAnalyzer.RequiredForCoverageSelection(testCase.Question,
                NegationAwareTopicAnalyzer.Analyze(
                    testCase.Question, SupportTopicCatalog.Create(product.ProductName)).PrimaryProfile ??
                TopicEntityAnalyzer.Extract(testCase.Question)),
            CandidateCount = sources.Count,
            SelectedCount = selection.Sources.Count,
            FinalSendCount = selection.Sources.Count,
            SelectionBudgetExceeded = selection.InsufficientEvidenceReasons.Contains("SelectionBudgetExceeded"),
            SearchCoverage = selection.SearchCoverage,
            SelectedCoverage = selection.FinalCoverage,
            MissingCoverage = selection.MissingCoverage,
            SelectorEngine = selection.SelectorEngine,
            Candidates = viewModels.Select((item, index) => BuildCandidate(item, index, rankedById, selectedIds)).ToList(),
            Selected = selection.Sources.Select((source, index) => new { Rank = index + 1, SourceType = source.SourceType, Role = index == 0 ? "Primary" : "Supporting" }).ToList(),
        };
        var outputPath = Environment.GetEnvironmentVariable("SCM_PHASE28_TRACE_OUTPUT") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "phase28-qac-cli-option-trace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(File.Exists(outputPath));
    }

    private static object BuildCandidate(
        SearchSourceViewModel item,
        int index,
        IReadOnlyDictionary<string, (string Id, int Rank, QuestionAwareEvidenceAssessment Item)> rankedById,
        IReadOnlySet<string> selectedIds)
    {
        var id = CandidateId(item, index);
        rankedById.TryGetValue(id, out var ranked);
        return new
        {
            Rank = index + 1,
            CandidateId = id,
            SourceType = item.SourceType,
            DocumentTitle = AnonymousTitle(item, index),
            Product = item.ProductName,
            Feature = item.SourceType is "PastCaseNote" or "PastAnswer" ? "Related" : "Project Analysis",
            Operation = HasValidateWorkflow(item) ? "Validate Upload" : "Analysis",
            Intent = "HowTo",
            KeywordScore = item.Source.LexicalScore,
            SemanticScore = item.Source.SemanticScore,
            HybridScore = item.Score,
            BaseScore = item.Score,
            QuestionAwareFinalScore = ranked.Item?.FinalScore,
            ProductMatch = ranked.Item?.ProductMatch,
            OperationMatch = ranked.Item is null ? (bool?)null : HasDirectAnalysisEvidence(item),
            TechnicalTokenScore = ranked.Item?.TechnicalTokenScore,
            Coverage = CoverageAnalyzer.ObserveForCoverageSelection(item.Text),
            SourceTrustScore = ranked.Item?.SourceTrustScore,
            ConflictPenalty = ranked.Item?.ConflictPenalty,
            Selected = selectedIds.Contains(item.Source.SourceId),
            SelectorInputRank = ranked.Rank == 0 ? (int?)null : ranked.Rank,
            SelectorOutputRank = selectedIds.Contains(item.Source.SourceId) ? (int?)(index + 1) : null,
            DirectAnalysisEvidence = HasDirectAnalysisEvidence(item),
            ValidateWorkflowEvidence = HasValidateWorkflow(item),
        };
    }

    private static bool HasDirectAnalysisEvidence(SearchSourceViewModel item) =>
        DocumentText(item).Contains("qacli analyze", StringComparison.OrdinalIgnoreCase) ||
        DocumentText(item).Contains("プロジェクトを解析", StringComparison.Ordinal);

    private static bool HasValidateWorkflow(SearchSourceViewModel item) =>
        DocumentText(item).Contains("qacli validate", StringComparison.OrdinalIgnoreCase) ||
        DocumentText(item).Contains("validate build", StringComparison.OrdinalIgnoreCase) ||
        DocumentText(item).Contains("アップロード", StringComparison.Ordinal);

    private static string DocumentText(SearchSourceViewModel item) => string.Join(
        '\n', item.Title, item.Source.QuestionText, item.Source.InternalMemo, item.Text);

    private static string AnonymousTitle(SearchSourceViewModel item, int index) =>
        item.SourceType is "PastCaseNote" or "PastAnswer"
            ? $"{item.SourceType}#{index + 1}"
            : $"{item.SourceType}#{index + 1}:{Hash(item.Title)}";

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 10);

    private static string CandidateId(SearchSourceViewModel item, int index) =>
        $"{(string.IsNullOrWhiteSpace(item.SourceId) ? "candidate" : item.SourceId)}#{index}";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SupportCaseManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record Phase28Case(string Id, string Product, string Type, string Question);
}
