using System.Text.Json;
using System.Reflection;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Ai.Core.Diagnostics;
using SupportCaseManager.Ai.Core.Drafts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Launch;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.AiAssistant.App.Appearance;
using SupportCaseManager.AiAssistant.App.Launch;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class LaunchContextApplyTests
{
    [Fact]
    public async Task InitializeFromCommandLineAsync_AppliesProductAndKeepsProductKnowledge()
    {
        var context = CreateContext() with
        {
            ProductName = "Klocwork",
            BaseFolder = @"D:\Context\Klocwork\Open",
            CloseFolder = @"D:\Context\Klocwork\Closed",
        };
        var services = CreateViewModel(context: context, settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Equal("Klocwork", services.ViewModel.ProductName);
        Assert.NotNull(services.ViewModel.SelectedProductKnowledge);
        Assert.Equal("Klocwork", services.ViewModel.SelectedProductKnowledge.ProductName);
        Assert.Equal(@"D:\Context\Klocwork\Open", services.ViewModel.BaseFolder);
        Assert.Equal(@"D:\Context\Klocwork\Closed", services.ViewModel.CloseFolder);
        Assert.Equal(@"D:\Manuals\Klocwork", Assert.Single(services.ViewModel.SelectedProductKnowledge.ManualFolders));
        Assert.Equal(@"D:\Manuals\Klocwork", services.ViewModel.ManualFolder);
    }

    [Fact]
    public async Task InitializeFromCommandLineAsync_CreatesContextProductWhenMissing()
    {
        var context = CreateContext() with
        {
            ProductName = "HelixQAC",
            BaseFolder = @"D:\Context\HelixQAC\Open",
            CloseFolder = @"D:\Context\HelixQAC\Closed",
        };
        var services = CreateViewModel(context: context, settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Equal("HelixQAC", services.ViewModel.ProductName);
        Assert.NotNull(services.ViewModel.SelectedProductKnowledge);
        Assert.Equal("HelixQAC", services.ViewModel.SelectedProductKnowledge.ProductName);
        Assert.Equal(@"D:\Context\HelixQAC\Open", services.ViewModel.SelectedProductKnowledge.BaseFolder);
        Assert.Equal(@"D:\Context\HelixQAC\Closed", services.ViewModel.SelectedProductKnowledge.CloseFolder);
        Assert.True(services.ViewModel.SelectedProductKnowledge.IsEnabled);
        Assert.Empty(services.ViewModel.SelectedProductKnowledge.ManualFolders);
        Assert.Empty(services.ViewModel.SelectedProductKnowledge.DocumentUrls);
        Assert.Contains("HelixQAC", services.ViewModel.Products.Select(static product => product.ProductName));
        Assert.Contains("HelixQAC", services.ViewModel.CurrentProductContextText);
        Assert.Contains("0", services.ViewModel.ManualFolderUsageText);
    }

    [Fact]
    public async Task InitializeFromCommandLineAsync_ShowsCurrentProductContextAndIndexFolder()
    {
        var services = CreateViewModel(context: CreateContext(), settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Contains("Klocwork", services.ViewModel.CurrentProductContextText);
        Assert.Contains(@"D:\ai-index", services.ViewModel.CurrentProductContextText);
        Assert.Contains("Klocwork", services.ViewModel.SelectedProductIndexFolder);
        Assert.Contains("1", services.ViewModel.ManualFolderUsageText);
    }

    [Fact]
    public async Task SaveSettingsAsync_AfterLaunchContextPreservesProductManualFoldersAndUrls()
    {
        var context = CreateContext() with
        {
            BaseFolder = @"D:\Context\Klocwork\Open",
            CloseFolder = @"D:\Context\Klocwork\Closed",
        };
        var services = CreateViewModel(context: context, settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });
        await InvokePrivateTaskAsync(services.ViewModel, "SaveSettingsAsync");

        Assert.NotNull(services.SettingsStore.SavedSettings);
        var saved = services.SettingsStore.SavedSettings;
        var product = Assert.Single(saved.Products);
        Assert.Equal("Klocwork", product.ProductName);
        Assert.Equal(@"D:\Manuals\Klocwork", Assert.Single(product.ManualFolders));
        Assert.Equal("https://example.test/klocwork", Assert.Single(product.DocumentUrls));
        Assert.Equal(@"D:\Context\Klocwork\Open", product.BaseFolder);
        Assert.Equal(@"D:\Context\Klocwork\Closed", product.CloseFolder);
    }

    [Fact]
    public async Task InitializeFromCommandLineAsync_AppliesCaseFields()
    {
        var context = CreateContext();
        var services = CreateViewModel(context: context, settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Equal(context.CaseFolderPath, services.ViewModel.CaseFolderPath);
        Assert.Equal(context.CompanyName, services.ViewModel.CompanyName);
        Assert.Equal(context.SupportNumber, services.ViewModel.SupportNumber);
        Assert.Equal(context.Status, services.ViewModel.Status);
        Assert.Equal("2026-06-02", services.ViewModel.ReceptionDate);
    }

    [Fact]
    public async Task InitializeFromCommandLineAsync_UsesInquiryTextThenSelectedTextThenCurrentNoteText()
    {
        var services = CreateViewModel(
            context: CreateContext() with
            {
                InquiryText = "問い合わせ本文",
                SelectedText = "選択テキスト",
                CurrentNoteText = "ノート本文",
            },
            settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Equal("問い合わせ本文", services.ViewModel.InquiryText);

        services = CreateViewModel(
            context: CreateContext() with
            {
                InquiryText = "",
                SelectedText = "選択テキスト",
                CurrentNoteText = "ノート本文",
            },
            settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Equal("選択テキスト", services.ViewModel.InquiryText);

        services = CreateViewModel(
            context: CreateContext() with
            {
                InquiryText = "",
                SelectedText = "",
                CurrentNoteText = "ノート本文",
            },
            settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Equal("ノート本文", services.ViewModel.InquiryText);
    }

    [Fact]
    public async Task InitializeFromCommandLineAsync_AppliesAdditionalInstructionAndCurrentNotePreview()
    {
        var context = CreateContext() with
        {
            NoteKind = "お客様への返信案",
            NoteFilePath = @"D:\Cases\00017581\reply_00017581.txt",
            CurrentNoteText = "既存ノート本文",
            AdditionalInstruction = "丁寧に回答してください。",
        };
        var services = CreateViewModel(context: context, settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Equal("丁寧に回答してください。", services.ViewModel.AdditionalInstruction);
        var note = Assert.Single(services.ViewModel.Notes);
        Assert.Equal("お客様への返信案", note.NoteKind);
        Assert.Equal(@"D:\Cases\00017581\reply_00017581.txt", note.FilePath);
        Assert.Equal("reply_00017581.txt", note.FileName);
        Assert.Equal("既存ノート本文", services.ViewModel.SelectedNoteText);
    }

    [Fact]
    public async Task InitializeFromCommandLineAsync_ContextReadFailureDoesNotCrashAndIsLogged()
    {
        var services = CreateViewModel(
            context: null,
            settings: CreateSettings(),
            launchException: new JsonException("context json failed"));

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions { ContextFilePath = "ai-context.json" });

        Assert.Contains("JsonException", services.ViewModel.ErrorText);
        Assert.Equal(1, services.Logger.ErrorCount);
        Assert.Contains("AI assistant operation failed.", services.Logger.LastErrorMessage);
    }

    [Fact]
    public async Task InitializeFromCommandLineAsync_NoContextKeepsNormalStartup()
    {
        var services = CreateViewModel(context: CreateContext(), settings: CreateSettings());

        await services.ViewModel.InitializeFromCommandLineAsync(new CommandLineOptions());

        Assert.Equal(0, services.LaunchReader.ReadCount);
        Assert.Equal(0, services.SettingsStore.LoadCount);
    }

    private static TestServices CreateViewModel(
        AiAssistantLaunchContext? context,
        AiAssistantSettings settings,
        Exception? launchException = null)
    {
        var logger = new CapturingDiagnosticLogger();
        var settingsStore = new FakeSettingsStore(settings);
        var launchReader = new FakeLaunchContextReader(context, launchException);
        var viewModel = new MainViewModel(
            settingsStore,
            new FakeCaseContextBuilder(),
            new FakeNoteSnapshotReader(),
            new FakeCaseIndexBuilder(),
            new FakeManualIndexBuilder(),
            new FakeProductScopedIndexService(),
            new FakeCaseKeywordSearcher(),
            new FakeManualKeywordSearcher(),
            new FakeProductScopedSearchService(),
            new InquiryFocusExtractor(),
            new FakeOllamaConnectionChecker(),
            new FakeSupportToolSettingsReader(),
            new ProductKnowledgeSettingsSynchronizer(),
            launchReader,
            _ => new FakeAnswerService(),
            new FakeDraftStore(),
            _ => logger,
            new NoopAppearanceService());

        return new TestServices(viewModel, logger, settingsStore, launchReader);
    }

    private static AiAssistantLaunchContext CreateContext()
    {
        return new AiAssistantLaunchContext
        {
            Source = "SupportCaseManager.App",
            ProductName = "Klocwork",
            BaseFolder = @"D:\Support\Klocwork\Open",
            CloseFolder = @"D:\Support\Klocwork\Closed",
            CaseFolderPath = @"D:\Support\Klocwork\Open\20260602(company_00017581)",
            CompanyName = "日本語株式会社",
            SupportNumber = "00017581",
            Status = "お客様へ返信中",
            ReceptionDate = new DateOnly(2026, 6, 2),
            NoteKind = "お客様への返信案",
            NoteFilePath = @"D:\Support\Klocwork\Open\reply_00017581.txt",
            CurrentNoteText = "既存ノート本文",
            InquiryText = "ライセンス認証エラーです。",
            AdditionalInstruction = "丁寧に回答してください。",
        };
    }

    private static AiAssistantSettings CreateSettings()
    {
        return new AiAssistantSettings
        {
            AiDataFolder = @"D:\ai-data",
            AiIndexFolder = @"D:\ai-index",
            SelectedProductName = "Klocwork",
            Products =
            [
                new ProductKnowledgeSettings
                {
                    ProductName = "Klocwork",
                    BaseFolder = @"D:\Support\Klocwork\Open",
                    CloseFolder = @"D:\Support\Klocwork\Closed",
                    ManualFolders = [@"D:\Manuals\Klocwork"],
                    DocumentUrls = ["https://example.test/klocwork"],
                    IsEnabled = true,
                },
            ],
        };
    }

    private static async Task InvokePrivateTaskAsync(MainViewModel viewModel, string methodName)
    {
        var method = typeof(MainViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(viewModel, []));
        await task;
    }

    private sealed record TestServices(
        MainViewModel ViewModel,
        CapturingDiagnosticLogger Logger,
        FakeSettingsStore SettingsStore,
        FakeLaunchContextReader LaunchReader);

    private sealed class NoopAppearanceService : IAppAppearanceService
    {
        public void Apply(string? uiLanguage, bool useDarkMode)
        {
        }
    }

    private sealed class FakeSettingsStore : IAiSettingsStore
    {
        private readonly AiAssistantSettings settings;

        public FakeSettingsStore(AiAssistantSettings settings)
        {
            this.settings = settings;
        }

        public int LoadCount { get; private set; }

        public AiAssistantSettings? SavedSettings { get; private set; }

        public Task<AiAssistantSettings> LoadAsync(string aiDataFolder, CancellationToken cancellationToken = default)
        {
            LoadCount += 1;
            return Task.FromResult(settings);
        }

        public Task SaveAsync(AiAssistantSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLaunchContextReader : IAiAssistantLaunchContextReader
    {
        private readonly AiAssistantLaunchContext? context;
        private readonly Exception? exception;

        public FakeLaunchContextReader(AiAssistantLaunchContext? context, Exception? exception)
        {
            this.context = context;
            this.exception = exception;
        }

        public int ReadCount { get; private set; }

        public Task<AiAssistantLaunchContext> ReadAsync(
            string contextFilePath,
            CancellationToken cancellationToken = default)
        {
            ReadCount += 1;
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(context ?? new AiAssistantLaunchContext());
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

        public Task<AiCaseIndexBuildResult> BuildCaseIndexAsync(ProductKnowledgeSettings product, string aiIndexFolder, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiCaseIndexBuildResult());
        }

        public Task<AiManualIndexBuildResult> BuildManualIndexAsync(ProductKnowledgeSettings product, string aiIndexFolder, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiManualIndexBuildResult());
        }

        public Task<AiOfficialDocumentIndexBuildResult> BuildOfficialDocumentIndexAsync(ProductKnowledgeSettings product, string aiIndexFolder, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiOfficialDocumentIndexBuildResult());
        }
    }

    private sealed class FakeCaseKeywordSearcher : IAiCaseKeywordSearcher
    {
        public Task<IReadOnlyList<SearchSource>> SearchAsync(string aiIndexFolder, string query, int maxResults = 8, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }
    }

    private sealed class FakeManualKeywordSearcher : IAiManualKeywordSearcher
    {
        public Task<IReadOnlyList<SearchSource>> SearchAsync(string aiIndexFolder, string query, int maxResults = 8, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }
    }

    private sealed class FakeProductScopedSearchService : IProductScopedSearchService
    {
        public Task<IReadOnlyList<SearchSource>> SearchPastCasesAsync(ProductKnowledgeSettings product, string aiIndexFolder, string query, int maxResults = 8, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }

        public Task<IReadOnlyList<SearchSource>> SearchManualsAsync(ProductKnowledgeSettings product, string aiIndexFolder, string query, int maxResults = 8, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }

        public Task<IReadOnlyList<SearchSource>> SearchOfficialDocumentsAsync(ProductKnowledgeSettings product, string aiIndexFolder, InquiryFocus inquiryFocus, int maxResults = 8, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }

        public Task<IReadOnlyList<SearchSource>> SearchAllAsync(ProductKnowledgeSettings product, string aiIndexFolder, InquiryFocus inquiryFocus, int maxResults = 8, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SearchSource>>([]);
        }
    }

    private sealed class FakeSupportToolSettingsReader : ISupportToolSettingsReader
    {
        public string? FindDefaultSettingsFilePath()
        {
            return null;
        }

        public Task<IReadOnlyList<SupportToolProductSettings>> ReadProductsAsync(string userSettingsFilePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SupportToolProductSettings>>([]);
        }
    }

    private sealed class FakeOllamaConnectionChecker : IOllamaConnectionChecker
    {
        public Task<OllamaConnectionCheckResult> CheckAsync(LlmProviderSettings settings, bool disableThinking = true, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OllamaConnectionCheckResult { IsSuccess = true });
        }
    }

    private sealed class FakeAnswerService : IAiAnswerService
    {
        public Task<AnswerDraftResult> GenerateDraftAsync(AnswerDraftRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AnswerDraftResult());
        }
    }

    private sealed class FakeDraftStore : IAiDraftStore
    {
        public Task<string> SaveAsync(AnswerDraftRequest request, AnswerDraftResult result, CancellationToken cancellationToken = default)
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

        public Task LogErrorAsync(string message, Exception? exception = null, CancellationToken cancellationToken = default)
        {
            ErrorCount += 1;
            LastErrorMessage = exception is null
                ? message
                : $"{message}: {exception.GetType().Name}: {exception.Message}";
            return Task.CompletedTask;
        }
    }
}
