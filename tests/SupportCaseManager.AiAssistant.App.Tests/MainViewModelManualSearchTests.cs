using System.Reflection;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Ai.Core.Diagnostics;
using SupportCaseManager.Ai.Core.Drafts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Launch;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.AiAssistant.App.Appearance;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class MainViewModelManualSearchTests
{
    [Fact]
    public async Task ManualSearch_UpdatesSummaryAndSendPlan()
    {
        var source = CreateManualSource();
        var services = CreateViewModel([source]);

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");

        Assert.Single(services.ViewModel.SearchResults);
        Assert.Single(services.ViewModel.FilteredSearchResults);
        Assert.Equal(1, services.ViewModel.SearchResultCount);
        Assert.Equal(1, services.ViewModel.FilteredSearchResultCount);
        Assert.Equal(1, services.ViewModel.SelectedEvidenceCount);
        Assert.Equal(1, services.ViewModel.ManualSelectedCount);
        Assert.Equal(1, services.ViewModel.EvidenceToSendCount);
        Assert.Equal(0, services.ViewModel.ExcludedByLimitCount);
        Assert.True(services.ViewModel.SearchResults[0].IsSelected);
        Assert.True(services.ViewModel.SearchResults[0].WillBeSentToLlm);
        Assert.Equal("Will send", services.ViewModel.SearchResults[0].SendStatusText);
    }

    [Fact]
    public async Task ManualSearch_AllAndManualFiltersKeepManualInSummary()
    {
        var services = CreateViewModel([CreateManualSource()]);

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");
        services.ViewModel.SourceTypeFilter = SearchSourceFiltering.All;

        Assert.Equal(1, services.ViewModel.SearchResultCount);
        Assert.Equal(1, services.ViewModel.FilteredSearchResultCount);
        Assert.Equal(1, services.ViewModel.ManualSelectedCount);

        services.ViewModel.SourceTypeFilter = SearchSourceFiltering.Manual;

        Assert.Equal(1, services.ViewModel.SearchResultCount);
        Assert.Equal(1, services.ViewModel.FilteredSearchResultCount);
        Assert.Equal(1, services.ViewModel.ManualSelectedCount);
        Assert.Equal(1, services.ViewModel.EvidenceToSendCount);
    }

    [Fact]
    public async Task ManualSearch_PastCaseFilterHidesManualButKeepsSelectionAndSources()
    {
        var services = CreateViewModel([CreateManualSource()]);

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");
        services.ViewModel.SourceTypeFilter = SearchSourceFiltering.PastCaseNote;

        Assert.Equal(1, services.ViewModel.SearchResultCount);
        Assert.Equal(0, services.ViewModel.FilteredSearchResultCount);
        Assert.Equal(1, services.ViewModel.SelectedEvidenceCount);
        Assert.Equal(1, services.ViewModel.ManualSelectedCount);
        Assert.Equal(1, services.ViewModel.EvidenceToSendCount);
        Assert.True(services.ViewModel.SearchResults[0].IsSelected);
        Assert.True(services.ViewModel.SearchResults[0].WillBeSentToLlm);
    }

    [Fact]
    public async Task ManualSearch_SelectedManualIsIncludedInAnswerDraftRequestSources()
    {
        var source = CreateManualSource();
        var services = CreateViewModel([source]);

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");
        var request = InvokePrivate<AnswerDraftRequest>(services.ViewModel, "BuildDraftRequest");

        var requestSource = Assert.Single(request.Sources);
        Assert.Equal(source.SourceId, requestSource.SourceId);
        Assert.Equal("Manual", requestSource.SourceType);
    }

    [Fact]
    public void BuildDraftRequest_UsesCheckmarxCuratedFactsWhenProductNameStillDefault()
    {
        var services = CreateViewModel([]);
        services.ViewModel.ProductName = "製品A";
        services.ViewModel.InquiryText = "CxSASTの最新バージョン教えてください。EPとHFの最新バージョンも教えてください。";
        services.ViewModel.SearchResults.Add(new SearchSourceViewModel(CreateOfficialSource(), isSelected: true));

        var request = InvokePrivate<AnswerDraftRequest>(services.ViewModel, "BuildDraftRequest");

        Assert.Equal("Checkmarx", request.Case.ProductName);
        Assert.Equal(AnswerReadiness.AutoAnswerable, request.FactResolution?.AnswerReadiness);
        Assert.Contains(request.FactResolution!.ResolvedFacts, fact =>
            fact.Key == FactKeys.LatestSastVersion &&
            fact.Value == "9.7.0" &&
            fact.SourceType == "Curated");
        Assert.Contains(request.FactResolution.ResolvedFacts, fact =>
            fact.Key == FactKeys.LatestEnginePackVersion &&
            fact.Value == "9.7.6" &&
            fact.SourceType == "Curated");
        Assert.Contains(request.FactResolution.ResolvedFacts, fact =>
            fact.Key == FactKeys.LatestHotfixVersion &&
            fact.Value == "HF10" &&
            fact.SourceType == "Curated");
    }

    [Fact]
    public async Task ManualSearch_ExceptionIsShownAndLogged()
    {
        var services = CreateViewModel([], new InvalidOperationException("manual search failed"));

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");

        Assert.Contains("InvalidOperationException", services.ViewModel.ErrorText);
        Assert.Contains("manual search failed", services.ViewModel.ErrorText);
        Assert.Contains("処理中にエラーが発生しました", services.ViewModel.StatusMessage);
        Assert.Equal(1, services.Logger.ErrorCount);
        Assert.Contains("AI assistant operation failed.", services.Logger.LastErrorMessage);
    }

    [Fact]
    public async Task GenerateDraftAsync_FakeProviderShowsFakeStatus()
    {
        var services = CreateViewModel([CreateManualSource()]);
        services.ViewModel.LlmProvider = "Fake";
        services.ViewModel.ChatModel = "fake-model";

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");
        await InvokePrivateTaskAsync(services.ViewModel, "GenerateDraftAsync");

        Assert.Contains("モック", services.ViewModel.StatusMessage);
        Assert.Contains("Fake", services.ViewModel.DraftProviderStatusText);
        Assert.Contains("fake-model", services.ViewModel.DraftProviderStatusText);
        Assert.Contains("Provider=Fake", services.ViewModel.LastOperationResult);
    }

    [Fact]
    public async Task GenerateDraftAsync_OllamaProviderShowsOllamaStatus()
    {
        var services = CreateViewModel([CreateManualSource()]);
        services.ViewModel.LlmProvider = "Ollama";
        services.ViewModel.ChatModel = "qwen2.5:3b";

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");
        await InvokePrivateTaskAsync(services.ViewModel, "GenerateDraftAsync");

        Assert.Contains("Ollama", services.ViewModel.StatusMessage);
        Assert.Contains("Ollama", services.ViewModel.DraftProviderStatusText);
        Assert.Contains("qwen2.5:3b", services.ViewModel.DraftProviderStatusText);
        Assert.Contains("Provider=Ollama", services.ViewModel.LastOperationResult);
    }

    [Fact]
    public async Task GenerateDraftAsync_OllamaFailureDoesNotFallbackToFake()
    {
        var services = CreateViewModel(
            [CreateManualSource()],
            answerService: new FailingAnswerService(new InvalidOperationException("ollama failed")));
        services.ViewModel.LlmProvider = "Ollama";
        services.ViewModel.ChatModel = "qwen2.5:3b";

        await InvokePrivateTaskAsync(services.ViewModel, "SearchManualsAsync");
        await InvokePrivateTaskAsync(services.ViewModel, "GenerateDraftAsync");

        Assert.Contains("Ollama", services.ViewModel.StatusMessage);
        Assert.Contains("ollama failed", services.ViewModel.ErrorText);
        Assert.Contains("Provider=Ollama", services.ViewModel.LastOperationResult);
        Assert.Contains("failed", services.ViewModel.LastOperationResult);
        Assert.DoesNotContain("Provider=Fake", services.ViewModel.LastOperationResult);
        Assert.Equal(1, services.Logger.ErrorCount);
    }

    private static TestServices CreateViewModel(
        IReadOnlyList<SearchSource> manualResults,
        Exception? manualSearchException = null,
        IAiAnswerService? answerService = null)
    {
        var logger = new CapturingDiagnosticLogger();
        var viewModel = new MainViewModel(
            new FakeSettingsStore(),
            new FakeCaseContextBuilder(),
            new FakeNoteSnapshotReader(),
            new FakeCaseIndexBuilder(),
            new FakeManualIndexBuilder(),
            new FakeProductScopedIndexService(),
            new FakeCaseKeywordSearcher(),
            new FakeManualKeywordSearcher(manualResults, manualSearchException),
            new FakeProductScopedSearchService(manualResults, manualSearchException),
            new InquiryFocusExtractor(),
            new FakeOllamaConnectionChecker(),
            new FakeSupportToolSettingsReader(),
            new ProductKnowledgeSettingsSynchronizer(),
            new FakeLaunchContextReader(),
            _ => answerService ?? new FakeAnswerService(),
            new FakeDraftStore(),
            _ => logger,
            new NoopAppearanceService())
        {
            InquiryText = """
                ライセンス認証エラーで製品が起動できません。
                ライセンスサーバー名、ポート番号、ファイアウォール設定を確認したいです。
                """,
            MaxEvidenceItems = 8,
            SourceTypeFilter = SearchSourceFiltering.All,
        };

        return new TestServices(viewModel, logger);
    }

    private static SearchSource CreateManualSource()
    {
        return new SearchSource
        {
            SourceId = "manual-license",
            SourceType = "Manual",
            Title = "license_error_manual - ライセンス認証エラー対応手順",
            Text = "ライセンス認証エラー、ライセンスサーバー名、ポート番号、ファイアウォール設定を確認します。",
            FilePath = @"D:\Manuals\license_error_manual.md",
            Score = 1.0,
        };
    }

    private static SearchSource CreateOfficialSource()
    {
        return new SearchSource
        {
            SourceId = "official-release-note",
            SourceType = "OfficialDoc",
            ProductName = "Checkmarx",
            Title = "Checkmarx CxSAST Release Notes",
            Text = "CxSAST release notes.",
            Url = "https://docs.checkmarx.com/en/34965-321884-release-notes-for-9-7-0.html",
            Score = 1.0,
        };
    }

    private static async Task InvokePrivateTaskAsync(MainViewModel viewModel, string methodName)
    {
        var method = typeof(MainViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(viewModel, []));
        await task;
    }

    private static T InvokePrivate<T>(MainViewModel viewModel, string methodName)
    {
        var method = typeof(MainViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(viewModel, []));
    }

    private sealed record TestServices(MainViewModel ViewModel, CapturingDiagnosticLogger Logger);

    private sealed class NoopAppearanceService : IAppAppearanceService
    {
        public void Apply(string? uiLanguage, bool useDarkMode)
        {
        }
    }

    private sealed class FakeSettingsStore : IAiSettingsStore
    {
        public Task<AiAssistantSettings> LoadAsync(string aiDataFolder, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiAssistantSettings { AiDataFolder = aiDataFolder });
        }

        public Task SaveAsync(AiAssistantSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCaseContextBuilder : ICaseContextBuilder
    {
        public Task<CaseContext> BuildFromCaseFolderAsync(
            string caseFolderPath,
            string? productName = null,
            string? baseFolder = null,
            string? closeFolder = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CaseContext());
        }
    }

    private sealed class FakeNoteSnapshotReader : INoteSnapshotReader
    {
        public Task<IReadOnlyList<NoteSnapshot>> ReadAllAsync(string caseFolderPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NoteSnapshot>>([]);
        }

        public Task<NoteSnapshot?> ReadAsync(string noteFilePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NoteSnapshot?>(null);
        }
    }

    private sealed class FakeCaseIndexBuilder : IAiCaseIndexBuilder
    {
        public Task<AiCaseIndexBuildResult> BuildAsync(string sourceFolder, string aiIndexFolder, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiCaseIndexBuildResult());
        }
    }

    private sealed class FakeManualIndexBuilder : IAiManualIndexBuilder
    {
        public Task<AiManualIndexBuildResult> BuildAsync(string manualFolder, string aiIndexFolder, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiManualIndexBuildResult());
        }

        public Task<AiManualIndexBuildResult> BuildManyAsync(
            IReadOnlyList<string> manualFolders,
            string aiIndexFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiManualIndexBuildResult());
        }
    }

    private sealed class FakeProductScopedIndexService : IProductScopedIndexService
    {
        public string GetProductIndexFolder(string aiIndexFolder, string productName)
        {
            return Path.Combine(aiIndexFolder, "products", productName);
        }

        public Task<AiCaseIndexBuildResult> BuildCaseIndexAsync(
            ProductKnowledgeSettings product,
            string aiIndexFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiCaseIndexBuildResult());
        }

        public Task<AiManualIndexBuildResult> BuildManualIndexAsync(
            ProductKnowledgeSettings product,
            string aiIndexFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiManualIndexBuildResult());
        }

        public Task<AiOfficialDocumentIndexBuildResult> BuildOfficialDocumentIndexAsync(
            ProductKnowledgeSettings product,
            string aiIndexFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiOfficialDocumentIndexBuildResult());
        }
    }

    private sealed class FakeCaseKeywordSearcher : IAiCaseKeywordSearcher
    {
        public Task<IReadOnlyList<SearchSource>> SearchAsync(
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }
    }

    private sealed class FakeManualKeywordSearcher : IAiManualKeywordSearcher
    {
        private readonly IReadOnlyList<SearchSource> results;
        private readonly Exception? exception;

        public FakeManualKeywordSearcher(IReadOnlyList<SearchSource> results, Exception? exception)
        {
            this.results = results;
            this.exception = exception;
        }

        public Task<IReadOnlyList<SearchSource>> SearchAsync(
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default)
        {
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(results);
        }
    }

    private sealed class FakeProductScopedSearchService : IProductScopedSearchService
    {
        private readonly IReadOnlyList<SearchSource> manualResults;
        private readonly Exception? manualSearchException;

        public FakeProductScopedSearchService(IReadOnlyList<SearchSource> manualResults, Exception? manualSearchException)
        {
            this.manualResults = manualResults;
            this.manualSearchException = manualSearchException;
        }

        public Task<IReadOnlyList<SearchSource>> SearchPastCasesAsync(
            ProductKnowledgeSettings product,
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }

        public Task<IReadOnlyList<SearchSource>> SearchManualsAsync(
            ProductKnowledgeSettings product,
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default)
        {
            if (manualSearchException is not null)
            {
                throw manualSearchException;
            }

            return Task.FromResult(manualResults);
        }

        public Task<IReadOnlyList<SearchSource>> SearchOfficialDocumentsAsync(
            ProductKnowledgeSettings product,
            string aiIndexFolder,
            InquiryFocus inquiryFocus,
            int maxResults = 8,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }

        public Task<IReadOnlyList<SearchSource>> SearchAllAsync(
            ProductKnowledgeSettings product,
            string aiIndexFolder,
            InquiryFocus inquiryFocus,
            int maxResults = 8,
            CancellationToken cancellationToken = default)
        {
            if (manualSearchException is not null)
            {
                throw manualSearchException;
            }

            return Task.FromResult(manualResults);
        }
    }

    private sealed class FakeSupportToolSettingsReader : ISupportToolSettingsReader
    {
        public string? FindDefaultSettingsFilePath()
        {
            return null;
        }

        public Task<IReadOnlyList<SupportToolProductSettings>> ReadProductsAsync(
            string userSettingsFilePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SupportToolProductSettings>>([]);
        }
    }

    private sealed class FakeLaunchContextReader : IAiAssistantLaunchContextReader
    {
        public Task<AiAssistantLaunchContext> ReadAsync(
            string contextFilePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiAssistantLaunchContext());
        }
    }

    private sealed class FakeOllamaConnectionChecker : IOllamaConnectionChecker
    {
        public Task<OllamaConnectionCheckResult> CheckAsync(
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OllamaConnectionCheckResult { IsSuccess = true });
        }
    }

    private sealed class FakeAnswerService : IAiAnswerService
    {
        public Task<AnswerDraftResult> GenerateDraftAsync(
            AnswerDraftRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AnswerDraftResult
            {
                CustomerReplyDraft = "回答案",
                InternalMemo = "社内メモ",
                Confidence = 0.5,
            });
        }
    }

    private sealed class FailingAnswerService : IAiAnswerService
    {
        private readonly Exception exception;

        public FailingAnswerService(Exception exception)
        {
            this.exception = exception;
        }

        public Task<AnswerDraftResult> GenerateDraftAsync(
            AnswerDraftRequest request,
            CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class FakeDraftStore : IAiDraftStore
    {
        public Task<string> SaveAsync(
            AnswerDraftRequest request,
            AnswerDraftResult result,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(@"D:\ai-data\drafts\draft.json");
        }
    }

    private sealed class CapturingDiagnosticLogger : IAiDiagnosticLogger
    {
        public int ErrorCount { get; private set; }

        public string LastErrorMessage { get; private set; } = string.Empty;

        public Task LogInfoAsync(string message, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task LogWarningAsync(string message, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task LogErrorAsync(
            string message,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            ErrorCount += 1;
            LastErrorMessage = exception is null
                ? message
                : $"{message}: {exception.GetType().Name}: {exception.Message}";
            return Task.CompletedTask;
        }
    }
}
