using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Core.Diagnostics;
using SupportCaseManager.Ai.Core.Drafts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Launch;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.AiAssistant.App.Appearance;
using SupportCaseManager.AiAssistant.App.Launch;
using SupportCaseManager.AiAssistant.App.Llm;
using SupportCaseManager.Core.Config;
using WinForms = System.Windows.Forms;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> AutoSavedProperties =
    [
        nameof(AiDataFolder), nameof(AiIndexFolder), nameof(BaseFolder), nameof(CloseFolder),
        nameof(ManualFolder), nameof(SupportToolSettingsFilePath), nameof(SelectedProductKnowledge),
        nameof(UiLanguage), nameof(UseDarkMode), nameof(LlmProvider), nameof(OllamaEndpoint),
        nameof(ChatModel), nameof(EmbeddingModel), nameof(Temperature), nameof(MaxOutputTokens),
        nameof(ContextWindowTokens), nameof(TimeoutSeconds), nameof(MaxEvidenceItems), nameof(MaxPromptChars),
        nameof(EnableCloudLlm), nameof(MaskSensitiveDataForCloud), nameof(DisableThinking),
        nameof(SkipGenerationWhenNoEvidence), nameof(EnableTopNFallback), nameof(HighScoreThreshold),
        nameof(MinimumDisplayScore), nameof(AnswerQualityMode),
        nameof(CodexExecutablePath),
    ];
    private const int ProductionMiniTestMinTimeoutSeconds = 60;
    private const int ProductionMiniTestMaxTimeoutSeconds = 60;
    private const int ProductionMiniTestMinOutputTokens = 8;
    private const int ProductionMiniTestMaxOutputTokens = 8;
    private const int ProductionMiniTestMinContextWindowTokens = 512;
    private const int ProductionMiniTestMaxContextWindowTokens = 512;
    private const string FastManualPreferredModelName = "qwen3:4b";
    private const string FastManualFallbackModelName = "qwen3:8b";
    private const int FastManualTimeoutSeconds = 90;
    private const int FastManualMaxPromptChars = 3500;
    private const int FastManualMaxEvidenceItems = 2;
    private const int FastManualMaxOutputTokens = 320;

    private readonly IAiSettingsStore settingsStore;
    private readonly ICaseContextBuilder caseContextBuilder;
    private readonly INoteSnapshotReader noteSnapshotReader;
    private readonly IAiCaseIndexBuilder caseIndexBuilder;
    private readonly IAiManualIndexBuilder manualIndexBuilder;
    private readonly IProductScopedIndexService productScopedIndexService;
    private readonly IAiCaseKeywordSearcher keywordSearcher;
    private readonly IAiManualKeywordSearcher manualKeywordSearcher;
    private readonly IProductScopedSearchService productScopedSearchService;
    private readonly IInquiryFocusExtractor inquiryFocusExtractor;
    private readonly IOllamaConnectionChecker ollamaConnectionChecker;
    private readonly ISupportToolSettingsReader supportToolSettingsReader;
    private readonly IProductKnowledgeSettingsSynchronizer productSettingsSynchronizer;
    private readonly IAiAssistantLaunchContextReader launchContextReader;
    private readonly Func<LlmProviderSettings, IAiAnswerService> answerServiceFactory;
    private readonly IAiDraftStore draftStore;
    private readonly Func<string, IAiDiagnosticLogger> loggerFactory;
    private readonly IAppAppearanceService appearanceService;
    private readonly ILlmClientFactory llmClientFactory;
    private CancellationTokenSource? autoSaveCancellation;
    private CancellationTokenSource? generationCancellation;
    private bool settingsLoaded;
    private bool isApplyingSettings;
    private bool ollamaModelsLoaded;
    private IReadOnlyList<ModelCapabilityProfile> modelCapabilityProfiles = [];

    private CaseContext? currentCaseContext;
    private AnswerDraftRequest? lastRequest;
    private AnswerDraftResult? lastResult;

    private string aiDataFolder = DefaultAiDataFolder();
    private string aiIndexFolder = DefaultAiIndexFolder();
    private string baseFolder = string.Empty;
    private string closeFolder = string.Empty;
    private string manualFolder = string.Empty;
    private string supportToolSettingsFilePath = string.Empty;
    private ProductKnowledgeViewModel? selectedProductKnowledge;
    private string externalContextProductName = string.Empty;
    private string selectedManualFolderPath = string.Empty;
    private string selectedDocumentUrl = string.Empty;
    private string newDocumentUrl = string.Empty;
    private string productKnowledgeStatusText = "Not loaded.";
    private string uiLanguage = "ja-JP";
    private bool useDarkMode;
    private string llmProvider = "Fake";
    private string ollamaEndpoint = "http://localhost:11434";
    private string chatModel = "qwen3:14b";
    private string answerQualityMode = SupportCaseManager.Ai.Contracts.AnswerQualityModes.Custom;
    private string embeddingModel = "nomic-embed-text";
    private double temperature = 0.2;
    private int maxOutputTokens = 800;
    private int contextWindowTokens = LlmProviderSettings.DefaultContextWindowTokens;
    private int timeoutSeconds = 120;
    private int maxEvidenceItems = 2;
    private int maxPromptChars = 6000;
    private bool enableCloudLlm;
    private bool maskSensitiveDataForCloud = true;
    private bool disableThinking = true;
    private bool skipGenerationWhenNoEvidence = true;
    private bool enableTopNFallback = true;
    private string caseFolderPath = string.Empty;
    private string productName = "製品A";
    private string companyName = "株式会社サンプル";
    private string customerName = string.Empty;
    private string supportNumber = "00001234";
    private string status = "対応中";
    private string receptionDate = "2026-06-02";
    private NoteSnapshot? selectedNote;
    private string inquiryText = "エラーの原因と対応方針を確認したいです。";
    private bool isSettingInquiryInternally;
    private bool inquiryManuallyEdited;
    private string additionalInstruction = "丁寧で簡潔に回答してください。";
    private int evidenceCount;
    private int promptApproxChars;
    private string customerReplyDraft = "まだ生成されていません。";
    private string internalMemo = string.Empty;
    private string needConfirmationsText = string.Empty;
    private string answerReadinessText = "-";
    private string resolvedFactsText = "(なし)";
    private string evidenceText = string.Empty;
    private string confidenceText = "-";
    private string warningsText = string.Empty;
    private string draftProviderStatusText = "Provider: -";
    private string statusMessage = "起動しました。モックデータを表示しています。";
    private string lastOperationResult = "未実行";
    private string errorText = string.Empty;
    private string savedDraftPath = string.Empty;
    private string indexBuildResultText = "Not built.";
    private string manualIndexBuildResultText = "Not built.";
    private string officialDocumentIndexBuildResultText = "Not built.";
    private string searchResultsText = "Not searched.";
    private IReadOnlyList<SearchSource> lastSearchSources = [];
    private IReadOnlyList<SearchSource> lastManualSearchSources = [];
    private IReadOnlyList<SearchSource> lastOfficialDocumentSearchSources = [];
    private SearchSource? selectedPastAnswerCandidate;
    private bool allowPastAnswerAutoSelection;
    private bool pastAnswerPolishRequested;
    private string pastAnswerCandidateText = "過去回答候補なし";
    private IReadOnlyList<SearchSource> lastUsedSources = [];
    private InquiryFocus? lastInquiryFocus;
    private string inquiryFocusSummaryText = string.Empty;
    private SearchSourceViewModel? selectedSearchResult;
    private string sourceTypeFilter = SearchSourceFiltering.All;
    private double highScoreThreshold = SearchSourceSummaryBuilder.DefaultAutoSelectMinimumScore;
    private double minimumDisplayScore;
    private int searchResultCount;
    private int filteredSearchResultCount;
    private int selectedEvidenceCount;
    private int pastCaseNoteSelectedCount;
    private int manualSelectedCount;
    private int officialDocSelectedCount;
    private int pastCaseNoteSendCount;
    private int manualSendCount;
    private int officialDocSendCount;
    private int evidenceToSendCount;
    private int excludedByLimitCount;
    private int usedEvidenceCount;
    private string usedSourcesText = "No draft has been generated.";
    private string evidenceLimitWarningText = string.Empty;
    private string evidenceSummaryText = string.Empty;
    private string ollamaConnectionResultText = "未確認";
    private string officialDocDiagnosticsText = string.Empty;
    private string modelRecommendationText = string.Empty;
    private string generationDiagnosticsText = string.Empty;
    private string ollamaProductionMiniTestResultText = "未実行";
    private string modelCompatibilityTestResultText = "未実行";
    private string knowledgeStatusText = "未作成";
    private string modelResolutionSource = ModelResolutionSources.Unresolved;
    private string generationState = "Ready";
    private string generationSkippedReason = string.Empty;
    private string ragDiagnosticsText = string.Empty;
    private bool isBusy;
    private int operationProgressPercent = 100;
    private string operationStage = "Ready";
    private bool isUpdatingPromptSummary;
    private string codexExecutablePath = string.Empty;
    private string? codexReplyUndo;
    private string? codexMemoUndo;
    private CodexChatViewModel? codex;

    public MainViewModel(
        IAiSettingsStore settingsStore,
        ICaseContextBuilder caseContextBuilder,
        INoteSnapshotReader noteSnapshotReader,
        IAiCaseIndexBuilder caseIndexBuilder,
        IAiManualIndexBuilder manualIndexBuilder,
        IProductScopedIndexService productScopedIndexService,
        IAiCaseKeywordSearcher keywordSearcher,
        IAiManualKeywordSearcher manualKeywordSearcher,
        IProductScopedSearchService productScopedSearchService,
        IInquiryFocusExtractor inquiryFocusExtractor,
        IOllamaConnectionChecker ollamaConnectionChecker,
        ISupportToolSettingsReader supportToolSettingsReader,
        IProductKnowledgeSettingsSynchronizer productSettingsSynchronizer,
        IAiAssistantLaunchContextReader launchContextReader,
        Func<LlmProviderSettings, IAiAnswerService> answerServiceFactory,
        IAiDraftStore draftStore,
        Func<string, IAiDiagnosticLogger> loggerFactory,
        IAppAppearanceService appearanceService,
        ILlmClientFactory? llmClientFactory = null)
    {
        this.settingsStore = settingsStore;
        this.caseContextBuilder = caseContextBuilder;
        this.noteSnapshotReader = noteSnapshotReader;
        this.caseIndexBuilder = caseIndexBuilder;
        this.manualIndexBuilder = manualIndexBuilder;
        this.productScopedIndexService = productScopedIndexService;
        this.keywordSearcher = keywordSearcher;
        this.manualKeywordSearcher = manualKeywordSearcher;
        this.productScopedSearchService = productScopedSearchService;
        this.inquiryFocusExtractor = inquiryFocusExtractor;
        this.ollamaConnectionChecker = ollamaConnectionChecker;
        this.supportToolSettingsReader = supportToolSettingsReader;
        this.productSettingsSynchronizer = productSettingsSynchronizer;
        this.launchContextReader = launchContextReader;
        this.answerServiceFactory = answerServiceFactory;
        this.draftStore = draftStore;
        this.loggerFactory = loggerFactory;
        this.appearanceService = appearanceService;
        this.llmClientFactory = llmClientFactory ?? new LlmClientFactory();

        LoadSettingsCommand = new AsyncRelayCommand(LoadSettingsAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        CheckOllamaConnectionCommand = new AsyncRelayCommand(CheckOllamaConnectionAsync);
        RunOllamaProductionMiniTestCommand = new AsyncRelayCommand(RunOllamaProductionMiniTestAsync);
        RefreshOllamaModelsCommand = new AsyncRelayCommand(RefreshOllamaModelsAsync);
        RunModelCompatibilityTestCommand = new AsyncRelayCommand(RunModelCompatibilityTestAsync);
        SelectAiDataFolderCommand = new RelayCommand(() => SelectFolder(value => AiDataFolder = value));
        SelectAiIndexFolderCommand = new RelayCommand(() => SelectFolder(value => AiIndexFolder = value));
        SelectBaseFolderCommand = new RelayCommand(() => SelectFolder(value => BaseFolder = value));
        SelectCloseFolderCommand = new RelayCommand(() => SelectFolder(value => CloseFolder = value));
        SelectManualFolderCommand = new RelayCommand(() => SelectFolder(value => ManualFolder = value));
        SelectSupportToolSettingsFileCommand = new RelayCommand(SelectSupportToolSettingsFile);
        SelectCodexExecutableCommand = new RelayCommand(SelectCodexExecutable);
        LoadSupportToolSettingsCommand = new AsyncRelayCommand(LoadSupportToolSettingsAsync);
        AddProductManualFolderCommand = new RelayCommand(AddProductManualFolder);
        RemoveProductManualFolderCommand = new RelayCommand(RemoveProductManualFolder);
        AddProductDocumentUrlCommand = new RelayCommand(AddProductDocumentUrl);
        RemoveProductDocumentUrlCommand = new RelayCommand(RemoveProductDocumentUrl);
        SaveProductKnowledgeSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        UseSelectedProductCommand = new RelayCommand(UseSelectedProduct);
        SelectCaseFolderCommand = new RelayCommand(() => SelectFolder(value => CaseFolderPath = value));
        LoadCaseCommand = new AsyncRelayCommand(LoadCaseAsync);
        ReloadNotesCommand = new AsyncRelayCommand(ReloadNotesAsync);
        BuildIndexCommand = new AsyncRelayCommand(BuildIndexAsync);
        BuildManualIndexCommand = new AsyncRelayCommand(BuildManualIndexAsync);
        BuildOfficialDocumentIndexCommand = new AsyncRelayCommand(BuildOfficialDocumentIndexAsync);
        UpdateKnowledgeCommand = new AsyncRelayCommand(() => UpdateKnowledgeAsync(KnowledgeUpdateScope.All, forceRebuild: false));
        RebuildKnowledgeCommand = new AsyncRelayCommand(() => UpdateKnowledgeAsync(KnowledgeUpdateScope.All, forceRebuild: true));
        UpdateManualKnowledgeCommand = new AsyncRelayCommand(() => UpdateKnowledgeAsync(KnowledgeUpdateScope.Manuals, forceRebuild: false));
        UpdatePastCaseKnowledgeCommand = new AsyncRelayCommand(() => UpdateKnowledgeAsync(KnowledgeUpdateScope.PastCases, forceRebuild: false));
        UpdateOfficialKnowledgeCommand = new AsyncRelayCommand(() => UpdateKnowledgeAsync(KnowledgeUpdateScope.OfficialDocs, forceRebuild: false));
        SearchPastCasesCommand = new AsyncRelayCommand(SearchPastCasesAsync);
        SearchManualsCommand = new AsyncRelayCommand(SearchManualsAsync);
        SelectVisibleSourcesCommand = new RelayCommand(SelectVisibleSources);
        ClearVisibleSourcesCommand = new RelayCommand(ClearVisibleSources);
        SelectHighScoreSourcesCommand = new RelayCommand(SelectHighScoreSources);
        ClearAllSourcesCommand = new RelayCommand(ClearAllSources);
        ToggleSelectedSourceCommand = new RelayCommand(ToggleSelectedSource);
        OpenSelectedSourceFileCommand = new RelayCommand(OpenSelectedSourceFile);
        OpenSelectedSourceFolderCommand = new RelayCommand(OpenSelectedSourceFolder);
        GenerateDraftCommand = new AsyncRelayCommand(GenerateDraftAsync);
        CancelGenerationCommand = new RelayCommand(
            CancelGeneration,
            () => generationCancellation is { IsCancellationRequested: false });
        GenerateHighQualityDraftCommand = new AsyncRelayCommand(GenerateHighQualityDraftAsync);
        UsePastAnswerCommand = new RelayCommand(UsePastAnswerWithoutLlm);
        ApplyPastAnswerCommand = new RelayCommand(ApplyPastAnswerToDraft);
        PolishPastAnswerCommand = new AsyncRelayCommand(PolishPastAnswerAsync);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);
        ClearInquiryCommand = new RelayCommand(ClearInquiry);
        CopyCustomerReplyCommand = new RelayCommand(() => CopyText(CustomerReplyDraft));
        CopyInternalMemoCommand = new RelayCommand(() => CopyText(InternalMemo));
        CopyAllCommand = new RelayCommand(() => CopyText(BuildFullDraftText()));
        SaveDraftCommand = new AsyncRelayCommand(SaveDraftAsync);
        WriteTestLogCommand = new AsyncRelayCommand(WriteTestLogAsync);
        OpenLogCommand = new RelayCommand(OpenLog);

        Notes.Add(new NoteSnapshot
        {
            NoteKind = "モックノート",
            FileName = "mock-note.txt",
            Text = "これは初期表示用のモックノートです。既存案件フォルダは読み取っていません。",
            IsCurrent = true,
        });
        SelectedNote = Notes.FirstOrDefault();
        UpdateModelRecommendationText();
        UpdatePromptSummary();
        ApplyAppearance();
    }

    public ObservableCollection<NoteSnapshot> Notes { get; } = [];

    public ObservableCollection<SearchSourceViewModel> SearchResults { get; } = [];

    public ObservableCollection<SearchSourceViewModel> FilteredSearchResults { get; } = [];

    public ObservableCollection<ProductKnowledgeViewModel> Products { get; } = [];

    public ObservableCollection<string> AvailableModels { get; } = [];

    public CodexChatViewModel? Codex => codex;

    public string CodexExecutablePath
    {
        get => codexExecutablePath;
        set => SetProperty(ref codexExecutablePath, value?.Trim() ?? string.Empty);
    }

    public IReadOnlyList<string> QualityModes { get; } =
    [
        SupportCaseManager.Ai.Contracts.AnswerQualityModes.Fast,
        SupportCaseManager.Ai.Contracts.AnswerQualityModes.Standard,
        SupportCaseManager.Ai.Contracts.AnswerQualityModes.Quality,
        SupportCaseManager.Ai.Contracts.AnswerQualityModes.Custom,
    ];

    public void AttachCodex(CodexChatViewModel codexViewModel)
    {
        codex = codexViewModel ?? throw new ArgumentNullException(nameof(codexViewModel));
        OnPropertyChanged(nameof(Codex));
    }

    public CodexCaseSnapshot BuildCodexCaseSnapshot()
    {
        var product = SelectedProductKnowledge;
        var selectedEvidence = SearchResults
            .Where(static item => item.WillBeSentToLlm || item.IsSelected)
            .OrderByDescending(static item =>
                item.SourceType.Contains("Curated", StringComparison.OrdinalIgnoreCase)
                || item.SourceType.Contains("Fact", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static item => item.Score ?? 0)
            .Select(static item => item.Source)
            .ToArray();
        return new CodexCaseSnapshot
        {
            ProductId = product?.ProductId is { } productId && productId != Guid.Empty ? productId : null,
            ProductName = ProductName,
            ProductPromptFilePath = product?.ProductPromptFilePath ?? string.Empty,
            SupportToolSettingsFilePath = SupportToolSettingsFilePath,
            SupportId = SupportNumber,
            CompanyName = CompanyName,
            CustomerName = CustomerName,
            Status = Status,
            ReceptionDate = ReceptionDate,
            CaseFolder = CaseFolderPath,
            InquiryFile = SelectedNote?.FileName ?? string.Empty,
            InquiryText = InquiryText,
            CustomerReplyDraft = CustomerReplyDraft,
            InternalMemo = InternalMemo,
            Evidence = selectedEvidence,
        };
    }

    public bool ApplyCodexReply(string text)
    {
        return ApplyCodexText(text, isReply: true);
    }

    public bool ApplyCodexMemo(string text)
    {
        return ApplyCodexText(text, isReply: false);
    }

    public void UndoCodexApplication(bool isReply)
    {
        if (isReply && codexReplyUndo is not null)
        {
            (CustomerReplyDraft, codexReplyUndo) = (codexReplyUndo, CustomerReplyDraft);
            StatusMessage = "返信案へのCodex反映を元に戻しました。ファイルは変更していません。";
        }
        else if (!isReply && codexMemoUndo is not null)
        {
            (InternalMemo, codexMemoUndo) = (codexMemoUndo, InternalMemo);
            StatusMessage = "調査メモへのCodex反映を元に戻しました。ファイルは変更していません。";
        }
    }

    private bool ApplyCodexText(string text, bool isReply)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var current = isReply ? CustomerReplyDraft : InternalMemo;
        var hasExisting = !string.IsNullOrWhiteSpace(current)
            && !string.Equals(current.Trim(), "まだ生成されていません。", StringComparison.Ordinal);
        var append = false;
        if (hasExisting)
        {
            var target = isReply ? "お客様への返信案" : "調査メモ";
            var result = System.Windows.MessageBox.Show(
                $"{target}に既存の文章があります。\n\nはい: 上書き\nいいえ: 末尾へ追加\nキャンセル: 反映しない",
                "Codex回答の反映",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
            {
                return false;
            }

            append = result == MessageBoxResult.No;
        }

        var next = CodexDraftTextApplicator.Apply(
            current,
            text,
            append ? CodexDraftApplyMode.Append : CodexDraftApplyMode.Overwrite);
        if (isReply)
        {
            codexReplyUndo = current;
            CustomerReplyDraft = next;
        }
        else
        {
            codexMemoUndo = current;
            InternalMemo = next;
        }

        StatusMessage = "Codex回答を編集欄へ反映しました。まだ案件ファイルには保存していません。";
        return true;
    }

    public string AiDataFolder
    {
        get => aiDataFolder;
        set
        {
            if (SetProperty(ref aiDataFolder, value))
            {
                OnPropertyChanged(nameof(LogFilePath));
            }
        }
    }

    public string AiIndexFolder
    {
        get => aiIndexFolder;
        set
        {
            if (SetProperty(ref aiIndexFolder, value))
            {
                OnPropertyChanged(nameof(SelectedProductIndexFolder));
                RefreshProductContextComputedProperties();
            }
        }
    }

    public string BaseFolder
    {
        get => baseFolder;
        set
        {
            if (SetProperty(ref baseFolder, value))
            {
                RefreshProductContextComputedProperties();
            }
        }
    }

    public string CloseFolder
    {
        get => closeFolder;
        set
        {
            if (SetProperty(ref closeFolder, value))
            {
                RefreshProductContextComputedProperties();
            }
        }
    }

    public string ManualFolder
    {
        get => manualFolder;
        set
        {
            if (SetProperty(ref manualFolder, value))
            {
                RefreshProductContextComputedProperties();
            }
        }
    }

    public string SupportToolSettingsFilePath
    {
        get => supportToolSettingsFilePath;
        set => SetProperty(ref supportToolSettingsFilePath, value);
    }

    public ProductKnowledgeViewModel? SelectedProductKnowledge
    {
        get => selectedProductKnowledge;
        set
        {
            if (SetProperty(ref selectedProductKnowledge, value))
            {
                ApplySelectedProductToCurrentFields();
                SelectedManualFolderPath = value?.ManualFolders.FirstOrDefault() ?? string.Empty;
                SelectedDocumentUrl = value?.DocumentUrls.FirstOrDefault() ?? string.Empty;
                OnPropertyChanged(nameof(SelectedProductIndexFolder));
                RefreshProductContextComputedProperties();
            }
        }
    }

    public string SelectedManualFolderPath
    {
        get => selectedManualFolderPath;
        set => SetProperty(ref selectedManualFolderPath, value);
    }

    public string SelectedDocumentUrl
    {
        get => selectedDocumentUrl;
        set => SetProperty(ref selectedDocumentUrl, value);
    }

    public string NewDocumentUrl
    {
        get => newDocumentUrl;
        set => SetProperty(ref newDocumentUrl, value);
    }

    public string ProductKnowledgeStatusText
    {
        get => productKnowledgeStatusText;
        private set => SetProperty(ref productKnowledgeStatusText, value);
    }

    public string CurrentProductContextText => BuildCurrentProductContextText();

    public string ManualFolderUsageText => BuildManualFolderUsageText();

    public string SelectedProductIndexFolder => SelectedProductKnowledge is null
        ? string.Empty
        : productScopedIndexService.GetProductIndexFolder(EffectiveAiIndexFolder(), SelectedProductKnowledge.ProductName);

    public string UiLanguage
    {
        get => uiLanguage;
        set
        {
            if (SetProperty(ref uiLanguage, string.IsNullOrWhiteSpace(value) ? "ja-JP" : value))
            {
                ApplyAppearance();
            }
        }
    }

    public bool UseDarkMode
    {
        get => useDarkMode;
        set
        {
            if (SetProperty(ref useDarkMode, value))
            {
                ApplyAppearance();
            }
        }
    }

    public string LlmProvider
    {
        get => llmProvider;
        set => SetProperty(ref llmProvider, value);
    }

    public string OllamaEndpoint
    {
        get => ollamaEndpoint;
        set => SetProperty(ref ollamaEndpoint, value);
    }

    public string ChatModel
    {
        get => chatModel;
        set
        {
            if (SetProperty(ref chatModel, value?.Trim() ?? string.Empty))
            {
                UpdateModelRecommendationText();
            }
        }
    }

    public string AnswerQualityMode
    {
        get => answerQualityMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? SupportCaseManager.Ai.Contracts.AnswerQualityModes.Custom
                : value;
            if (SetProperty(ref answerQualityMode, normalized) && !isApplyingSettings)
            {
                ApplyQualityModeProfile(normalized);
            }
        }
    }

    public string EmbeddingModel
    {
        get => embeddingModel;
        set => SetProperty(ref embeddingModel, value);
    }

    public double Temperature
    {
        get => temperature;
        set => SetProperty(ref temperature, value);
    }

    public int MaxOutputTokens
    {
        get => maxOutputTokens;
        set => SetProperty(ref maxOutputTokens, value);
    }

    public int ContextWindowTokens
    {
        get => contextWindowTokens;
        set => SetProperty(ref contextWindowTokens, value);
    }

    public int TimeoutSeconds
    {
        get => timeoutSeconds;
        set => SetProperty(ref timeoutSeconds, value);
    }

    public int MaxEvidenceItems
    {
        get => maxEvidenceItems;
        set
        {
            if (SetProperty(ref maxEvidenceItems, value))
            {
                UpdatePromptSummary();
            }
        }
    }

    public bool SkipGenerationWhenNoEvidence
    {
        get => skipGenerationWhenNoEvidence;
        set => SetProperty(ref skipGenerationWhenNoEvidence, value);
    }

    public bool EnableTopNFallback
    {
        get => enableTopNFallback;
        set
        {
            if (SetProperty(ref enableTopNFallback, value))
            {
                UpdatePromptSummary();
            }
        }
    }

    public int MaxPromptChars
    {
        get => maxPromptChars;
        set => SetProperty(ref maxPromptChars, value);
    }

    public bool EnableCloudLlm
    {
        get => enableCloudLlm;
        set => SetProperty(ref enableCloudLlm, value);
    }

    public bool MaskSensitiveDataForCloud
    {
        get => maskSensitiveDataForCloud;
        set => SetProperty(ref maskSensitiveDataForCloud, value);
    }

    public bool DisableThinking
    {
        get => disableThinking;
        set => SetProperty(ref disableThinking, value);
    }

    public string OfficialDocDiagnosticsText
    {
        get => officialDocDiagnosticsText;
        private set => SetProperty(ref officialDocDiagnosticsText, value);
    }

    public string ModelRecommendationText
    {
        get => modelRecommendationText;
        private set => SetProperty(ref modelRecommendationText, value);
    }

    public string GenerationDiagnosticsText
    {
        get => generationDiagnosticsText;
        private set => SetProperty(ref generationDiagnosticsText, value);
    }

    public string OllamaProductionMiniTestResultText
    {
        get => ollamaProductionMiniTestResultText;
        private set => SetProperty(ref ollamaProductionMiniTestResultText, value);
    }

    public string ModelCompatibilityTestResultText
    {
        get => modelCompatibilityTestResultText;
        private set => SetProperty(ref modelCompatibilityTestResultText, value);
    }

    public string KnowledgeStatusText
    {
        get => knowledgeStatusText;
        private set => SetProperty(ref knowledgeStatusText, value);
    }

    public string ModelResolutionSource
    {
        get => modelResolutionSource;
        private set => SetProperty(ref modelResolutionSource, value);
    }

    public string GenerationState
    {
        get => generationState;
        private set => SetProperty(ref generationState, value);
    }

    public string GenerationSkippedReason
    {
        get => generationSkippedReason;
        private set => SetProperty(ref generationSkippedReason, value);
    }

    public string RagDiagnosticsText
    {
        get => ragDiagnosticsText;
        private set => SetProperty(ref ragDiagnosticsText, value);
    }

    public string CaseFolderPath
    {
        get => caseFolderPath;
        set => SetProperty(ref caseFolderPath, value);
    }

    public string ProductName
    {
        get => productName;
        set
        {
            if (SetProperty(ref productName, value))
            {
                RefreshProductContextComputedProperties();
            }
        }
    }

    public string CompanyName
    {
        get => companyName;
        set => SetProperty(ref companyName, value);
    }

    public string CustomerName
    {
        get => customerName;
        set => SetProperty(ref customerName, value);
    }

    public string SupportNumber
    {
        get => supportNumber;
        set => SetProperty(ref supportNumber, value);
    }

    public string Status
    {
        get => status;
        set => SetProperty(ref status, value);
    }

    public string ReceptionDate
    {
        get => receptionDate;
        set => SetProperty(ref receptionDate, value);
    }

    public NoteSnapshot? SelectedNote
    {
        get => selectedNote;
        set
        {
            if (SetProperty(ref selectedNote, value))
            {
                OnPropertyChanged(nameof(SelectedNoteText));
            }
        }
    }

    public string SelectedNoteText => SelectedNote?.Text ?? string.Empty;

    public string InquiryText
    {
        get => inquiryText;
        set
        {
            if (SetProperty(ref inquiryText, value))
            {
                if (!isSettingInquiryInternally)
                {
                    inquiryManuallyEdited = true;
                    selectedPastAnswerCandidate = null;
                    PastAnswerCandidateText = "問い合わせ内容が変更されました。過去回答を再検索してください。";
                }

                UpdatePromptSummary();
            }
        }
    }

    public string AdditionalInstruction
    {
        get => additionalInstruction;
        set
        {
            if (SetProperty(ref additionalInstruction, value))
            {
                UpdatePromptSummary();
            }
        }
    }

    public int EvidenceCount
    {
        get => evidenceCount;
        private set => SetProperty(ref evidenceCount, value);
    }

    public int PromptApproxChars
    {
        get => promptApproxChars;
        private set => SetProperty(ref promptApproxChars, value);
    }

    public string CustomerReplyDraft
    {
        get => customerReplyDraft;
        set => SetProperty(ref customerReplyDraft, value);
    }

    public string InternalMemo
    {
        get => internalMemo;
        set => SetProperty(ref internalMemo, value);
    }

    public string NeedConfirmationsText
    {
        get => needConfirmationsText;
        private set => SetProperty(ref needConfirmationsText, value);
    }

    public string AnswerReadinessText
    {
        get => answerReadinessText;
        private set => SetProperty(ref answerReadinessText, value);
    }

    public string ResolvedFactsText
    {
        get => resolvedFactsText;
        private set => SetProperty(ref resolvedFactsText, value);
    }

    public string EvidenceText
    {
        get => evidenceText;
        private set => SetProperty(ref evidenceText, value);
    }

    public string ConfidenceText
    {
        get => confidenceText;
        private set => SetProperty(ref confidenceText, value);
    }

    public string WarningsText
    {
        get => warningsText;
        private set => SetProperty(ref warningsText, value);
    }

    public string DraftProviderStatusText
    {
        get => draftProviderStatusText;
        private set => SetProperty(ref draftProviderStatusText, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string LastOperationResult
    {
        get => lastOperationResult;
        private set => SetProperty(ref lastOperationResult, value);
    }

    public string ErrorText
    {
        get => errorText;
        private set => SetProperty(ref errorText, value);
    }

    public string SavedDraftPath
    {
        get => savedDraftPath;
        private set => SetProperty(ref savedDraftPath, value);
    }

    public string OllamaConnectionResultText
    {
        get => ollamaConnectionResultText;
        private set => SetProperty(ref ollamaConnectionResultText, value);
    }

    public string IndexBuildResultText
    {
        get => indexBuildResultText;
        private set => SetProperty(ref indexBuildResultText, value);
    }

    public string ManualIndexBuildResultText
    {
        get => manualIndexBuildResultText;
        private set => SetProperty(ref manualIndexBuildResultText, value);
    }

    public string OfficialDocumentIndexBuildResultText
    {
        get => officialDocumentIndexBuildResultText;
        private set => SetProperty(ref officialDocumentIndexBuildResultText, value);
    }

    public string SearchResultsText
    {
        get => searchResultsText;
        private set => SetProperty(ref searchResultsText, value);
    }

    public string PastAnswerCandidateText
    {
        get => pastAnswerCandidateText;
        private set => SetProperty(ref pastAnswerCandidateText, value);
    }

    public string InquiryFocusSummaryText
    {
        get => inquiryFocusSummaryText;
        private set => SetProperty(ref inquiryFocusSummaryText, value);
    }

    public SearchSourceViewModel? SelectedSearchResult
    {
        get => selectedSearchResult;
        set => SetProperty(ref selectedSearchResult, value);
    }

    public string SourceTypeFilter
    {
        get => sourceTypeFilter;
        set
        {
            if (SetProperty(ref sourceTypeFilter, string.IsNullOrWhiteSpace(value) ? SearchSourceFiltering.All : value))
            {
                RefreshFilteredSearchResults();
            }
        }
    }

    public double HighScoreThreshold
    {
        get => highScoreThreshold;
        set
        {
            if (SetProperty(ref highScoreThreshold, Math.Clamp(value, 0.0, 1.0)))
            {
                UpdatePromptSummary();
            }
        }
    }

    public double MinimumDisplayScore
    {
        get => minimumDisplayScore;
        set
        {
            if (SetProperty(ref minimumDisplayScore, Math.Clamp(value, 0.0, 1.0)))
            {
                RefreshFilteredSearchResults();
            }
        }
    }

    public int SearchResultCount
    {
        get => searchResultCount;
        private set => SetProperty(ref searchResultCount, value);
    }

    public int FilteredSearchResultCount
    {
        get => filteredSearchResultCount;
        private set => SetProperty(ref filteredSearchResultCount, value);
    }

    public int SelectedEvidenceCount
    {
        get => selectedEvidenceCount;
        private set => SetProperty(ref selectedEvidenceCount, value);
    }

    public int PastCaseNoteSelectedCount
    {
        get => pastCaseNoteSelectedCount;
        private set => SetProperty(ref pastCaseNoteSelectedCount, value);
    }

    public int ManualSelectedCount
    {
        get => manualSelectedCount;
        private set => SetProperty(ref manualSelectedCount, value);
    }

    public int OfficialDocSelectedCount
    {
        get => officialDocSelectedCount;
        private set => SetProperty(ref officialDocSelectedCount, value);
    }

    public int PastCaseNoteSendCount
    {
        get => pastCaseNoteSendCount;
        private set => SetProperty(ref pastCaseNoteSendCount, value);
    }

    public int ManualSendCount
    {
        get => manualSendCount;
        private set => SetProperty(ref manualSendCount, value);
    }

    public int OfficialDocSendCount
    {
        get => officialDocSendCount;
        private set => SetProperty(ref officialDocSendCount, value);
    }

    public int EvidenceToSendCount
    {
        get => evidenceToSendCount;
        private set => SetProperty(ref evidenceToSendCount, value);
    }

    public int ExcludedByLimitCount
    {
        get => excludedByLimitCount;
        private set => SetProperty(ref excludedByLimitCount, value);
    }

    public int UsedEvidenceCount
    {
        get => usedEvidenceCount;
        private set => SetProperty(ref usedEvidenceCount, value);
    }

    public string UsedSourcesText
    {
        get => usedSourcesText;
        private set => SetProperty(ref usedSourcesText, value);
    }

    public string EvidenceLimitWarningText
    {
        get => evidenceLimitWarningText;
        private set => SetProperty(ref evidenceLimitWarningText, value);
    }

    public string EvidenceSummaryText
    {
        get => evidenceSummaryText;
        private set => SetProperty(ref evidenceSummaryText, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                CancelGenerationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int OperationProgressPercent
    {
        get => operationProgressPercent;
        private set
        {
            if (SetProperty(ref operationProgressPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(OperationProgressText));
            }
        }
    }

    public string OperationStage
    {
        get => operationStage;
        private set
        {
            if (SetProperty(ref operationStage, value))
            {
                OnPropertyChanged(nameof(OperationProgressText));
            }
        }
    }

    public string OperationProgressText => $"{OperationStage} {OperationProgressPercent}%";

    public string LogFilePath => Path.Combine(EffectiveAiDataFolder(), "logs", "AiAssistant.log");

    public AsyncRelayCommand LoadSettingsCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand CheckOllamaConnectionCommand { get; }
    public AsyncRelayCommand RunOllamaProductionMiniTestCommand { get; }
    public AsyncRelayCommand RefreshOllamaModelsCommand { get; }
    public AsyncRelayCommand RunModelCompatibilityTestCommand { get; }
    public RelayCommand SelectAiDataFolderCommand { get; }
    public RelayCommand SelectAiIndexFolderCommand { get; }
    public RelayCommand SelectBaseFolderCommand { get; }
    public RelayCommand SelectCloseFolderCommand { get; }
    public RelayCommand SelectManualFolderCommand { get; }
    public RelayCommand SelectSupportToolSettingsFileCommand { get; }
    public AsyncRelayCommand LoadSupportToolSettingsCommand { get; }
    public RelayCommand AddProductManualFolderCommand { get; }
    public RelayCommand RemoveProductManualFolderCommand { get; }
    public RelayCommand AddProductDocumentUrlCommand { get; }
    public RelayCommand RemoveProductDocumentUrlCommand { get; }
    public AsyncRelayCommand SaveProductKnowledgeSettingsCommand { get; }
    public RelayCommand UseSelectedProductCommand { get; }
    public RelayCommand SelectCaseFolderCommand { get; }
    public AsyncRelayCommand LoadCaseCommand { get; }
    public AsyncRelayCommand ReloadNotesCommand { get; }
    public AsyncRelayCommand BuildIndexCommand { get; }
    public AsyncRelayCommand BuildManualIndexCommand { get; }
    public AsyncRelayCommand BuildOfficialDocumentIndexCommand { get; }
    public AsyncRelayCommand UpdateKnowledgeCommand { get; }
    public AsyncRelayCommand RebuildKnowledgeCommand { get; }
    public AsyncRelayCommand UpdateManualKnowledgeCommand { get; }
    public AsyncRelayCommand UpdatePastCaseKnowledgeCommand { get; }
    public AsyncRelayCommand UpdateOfficialKnowledgeCommand { get; }
    public AsyncRelayCommand SearchPastCasesCommand { get; }
    public AsyncRelayCommand SearchManualsCommand { get; }
    public RelayCommand SelectVisibleSourcesCommand { get; }
    public RelayCommand ClearVisibleSourcesCommand { get; }
    public RelayCommand SelectHighScoreSourcesCommand { get; }
    public RelayCommand ClearAllSourcesCommand { get; }
    public RelayCommand ToggleSelectedSourceCommand { get; }
    public RelayCommand OpenSelectedSourceFileCommand { get; }
    public RelayCommand OpenSelectedSourceFolderCommand { get; }
    public AsyncRelayCommand GenerateDraftCommand { get; }
    public RelayCommand CancelGenerationCommand { get; }
    public AsyncRelayCommand GenerateHighQualityDraftCommand { get; }
    public RelayCommand UsePastAnswerCommand { get; }
    public RelayCommand ApplyPastAnswerCommand { get; }
    public AsyncRelayCommand PolishPastAnswerCommand { get; }
    public AsyncRelayCommand ResetSettingsCommand { get; }
    public RelayCommand ClearInquiryCommand { get; }
    public RelayCommand CopyCustomerReplyCommand { get; }
    public RelayCommand CopyInternalMemoCommand { get; }
    public RelayCommand CopyAllCommand { get; }
    public AsyncRelayCommand SaveDraftCommand { get; }
    public AsyncRelayCommand WriteTestLogCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand SelectCodexExecutableCommand { get; }

    public async Task InitializeFromCommandLineAsync(CommandLineOptions options)
    {
        options ??= new CommandLineOptions();

        if (options.Warnings.Count > 0)
        {
            var warningText = string.Join(" ", options.Warnings);
            StatusMessage = warningText;
            await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync($"Command line warning. Count={options.Warnings.Count}");
        }

        await RunBusyAsync(async () =>
        {
            SetOperationProgress(5, "設定を読み込んでいます");
            AiAssistantSettings settings;
            try
            {
                settings = await settingsStore.LoadAsync(EffectiveAiDataFolder());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                settings = new AiAssistantSettings { AiDataFolder = EffectiveAiDataFolder() };
                StatusMessage = "設定の読込みに失敗したため、既定値で起動しました。";
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync($"Settings load failed. {ex.GetType().Name}: {ex.Message}");
            }

            ApplySettings(settings);
            settingsLoaded = true;
            SetOperationProgress(15, "設定を反映しました");

            if (!string.IsNullOrWhiteSpace(options.ContextFilePath))
            {
                var context = await launchContextReader.ReadAsync(options.ContextFilePath);
                if (!string.IsNullOrWhiteSpace(context.SupportToolSettingsFilePath)
                    && File.Exists(context.SupportToolSettingsFilePath))
                {
                    try
                    {
                        var supportProducts = await supportToolSettingsReader.ReadProductsAsync(context.SupportToolSettingsFilePath);
                        var synchronized = productSettingsSynchronizer.Synchronize(
                            BuildSettings() with { SupportToolSettingsFilePath = context.SupportToolSettingsFilePath },
                            supportProducts);
                        ApplySettings(synchronized);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                    {
                        ProductKnowledgeStatusText = $"製品設定を同期できませんでした。起動情報だけで続行します: {ex.Message}";
                    }
                }

                var suppliedCaseFolder = context.CaseFolderPath;
                context = LaunchContextCaseFolderResolver.Resolve(context);
                var caseFolderRecovered = !string.Equals(
                    suppliedCaseFolder,
                    context.CaseFolderPath,
                    StringComparison.OrdinalIgnoreCase);

                ApplyLaunchContext(context);

                var caseFolderExists = !string.IsNullOrWhiteSpace(context.CaseFolderPath)
                    && Directory.Exists(context.CaseFolderPath);
                var noteFileExists = !string.IsNullOrWhiteSpace(context.NoteFilePath)
                    && File.Exists(context.NoteFilePath);

                if (caseFolderExists)
                {
                    SetOperationProgress(25, "案件ノートを読み込んでいます");
                    currentCaseContext = await caseContextBuilder.BuildFromCaseFolderAsync(
                        context.CaseFolderPath,
                        ProductName,
                        BaseFolder,
                        CloseFolder);
                    ApplyCaseContext(currentCaseContext);
                    ApplyPreferredCustomerInquiry(currentCaseContext.Notes);

                    SetOperationProgress(45, "過去回答候補を検索しています");
                    await RefreshPastAnswerCandidateCoreAsync();
                }

                LastOperationResult = FormatLaunchContextDiagnostic(context, caseFolderExists, noteFileExists);
                if (caseFolderRecovered)
                {
                    LastOperationResult += $"案件フォルダ自動補正: {suppliedCaseFolder} -> {context.CaseFolderPath}{Environment.NewLine}";
                }
                ProductKnowledgeStatusText = string.IsNullOrWhiteSpace(context.ProductName)
                    ? ProductKnowledgeStatusText
                    : $"外部コンテキスト製品: {context.ProductName}";
                StatusMessage = caseFolderRecovered
                    ? "製品フォルダから案件を再検出し、案件情報を自動読込みしました。"
                    : "設定と案件情報を自動読込みしました。";

                await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                    $"Launch context loaded. Source={SanitizeLogToken(context.Source)}; ProductName={SanitizeLogToken(context.ProductName)}; CaseFolderExists={caseFolderExists}; NoteFileExists={noteFileExists}; CaseFolderRecovered={caseFolderRecovered}");
            }

            SetOperationProgress(75, "ナレッジ状態を確認しています");
            await RefreshKnowledgeStatusCoreAsync();
            SetOperationProgress(95, "起動準備を完了しています");
        });
        await RunBusyAsync(RefreshOllamaModelsCoreAsync, clearExistingError: false);
        StartLocalKnowledgeRefreshInBackground();
    }

    public void ApplyLaunchContext(AiAssistantLaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.IsNullOrWhiteSpace(context.SupportToolSettingsFilePath))
        {
            SupportToolSettingsFilePath = context.SupportToolSettingsFilePath;
        }

        if (!string.IsNullOrWhiteSpace(context.ProductName))
        {
            externalContextProductName = context.ProductName.Trim();
            EnsureLaunchContextProductSelected(context);
            ProductName = externalContextProductName;
        }

        if (!string.IsNullOrWhiteSpace(context.BaseFolder))
        {
            BaseFolder = context.BaseFolder;
        }

        if (!string.IsNullOrWhiteSpace(context.CloseFolder))
        {
            CloseFolder = context.CloseFolder;
        }

        CaseFolderPath = context.CaseFolderPath;
        CompanyName = context.CompanyName;
        CustomerName = context.CustomerName;
        SupportNumber = context.SupportNumber;
        Status = context.Status;
        ReceptionDate = context.ReceptionDate?.ToString("yyyy-MM-dd") ?? string.Empty;

        var inquiry = FirstNonWhiteSpace(context.InquiryText, context.SelectedText, context.CurrentNoteText);
        if (!string.IsNullOrWhiteSpace(inquiry))
        {
            var hasExplicitInquiry = !string.IsNullOrWhiteSpace(context.SelectedText)
                || (!string.IsNullOrWhiteSpace(context.InquiryText)
                    && !string.Equals(
                        context.InquiryText.Trim(),
                        context.CurrentNoteText?.Trim(),
                        StringComparison.Ordinal));
            SetInquiryTextInternally(inquiry, hasExplicitInquiry);
        }

        if (!string.IsNullOrWhiteSpace(context.AdditionalInstruction))
        {
            AdditionalInstruction = context.AdditionalInstruction;
        }

        ApplyLaunchContextNote(context);
        currentCaseContext = BuildCurrentCaseContext();
        RefreshProductContextComputedProperties();
        UpdatePromptSummary();
    }

    private async Task LoadSettingsAsync()
    {
        await RunBusyAsync(async () =>
        {
            var settings = await settingsStore.LoadAsync(EffectiveAiDataFolder());
            ApplySettings(settings);
            StatusMessage = "設定を読み込みました。";
            LastOperationResult = "設定読み込み完了";
        });
    }

    private async Task SaveSettingsAsync()
    {
        await RunBusyAsync(async () =>
        {
            SynchronizeSelectedProductFromCurrentFields();
            var settings = BuildSettings();
            await settingsStore.SaveAsync(settings);
            var selectedProduct = settings.Products.FirstOrDefault(product =>
                string.Equals(product.ProductName, settings.SelectedProductName, StringComparison.OrdinalIgnoreCase));
            StatusMessage = selectedProduct is null
                ? "設定を保存しました。"
                : $"製品別設定を保存しました。製品名: {selectedProduct.ProductName} / マニュアルフォルダ数: {selectedProduct.ManualFolders.Count} / 公式URL数: {selectedProduct.DocumentUrls.Count}";
            LastOperationResult = selectedProduct is null
                ? "設定保存完了"
                : $"製品別設定を保存しました。\n製品名: {selectedProduct.ProductName}\nマニュアルフォルダ数: {selectedProduct.ManualFolders.Count}\n公式URL数: {selectedProduct.DocumentUrls.Count}";
            ProductKnowledgeStatusText = LastOperationResult;
            RefreshProductContextComputedProperties();
        });
    }

    private async Task LoadSupportToolSettingsAsync()
    {
        await RunBusyAsync(async () =>
        {
            var settingsFilePath = SupportToolSettingsFilePath;
            if (string.IsNullOrWhiteSpace(settingsFilePath))
            {
                settingsFilePath = supportToolSettingsReader.FindDefaultSettingsFilePath();
            }

            if (string.IsNullOrWhiteSpace(settingsFilePath))
            {
                ProductKnowledgeStatusText = "既存サポートツールの user-settings.json が見つかりません。ファイルを選択してください。";
                StatusMessage = ProductKnowledgeStatusText;
                return;
            }

            var supportProducts = await supportToolSettingsReader.ReadProductsAsync(settingsFilePath);
            SupportToolSettingsFilePath = settingsFilePath;
            var synchronized = productSettingsSynchronizer.Synchronize(
                BuildSettings() with { SupportToolSettingsFilePath = settingsFilePath },
                supportProducts);

            ApplySettings(synchronized);
            settingsLoaded = true;
            await settingsStore.SaveAsync(BuildSettings());
            ProductKnowledgeStatusText = $"既存設定を読み込みました。Products={supportProducts.Count}";
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync($"Support tool settings loaded. Products={supportProducts.Count}");
            StatusMessage = ProductKnowledgeStatusText;
            LastOperationResult = "Support tool settings load completed.";
        });
    }

    private void SelectSupportToolSettingsFile()
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "既存サポートツールの user-settings.json を選択",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "user-settings.json",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            SupportToolSettingsFilePath = dialog.FileName;
        }
    }

    private void AddProductManualFolder()
    {
        if (SelectedProductKnowledge is null)
        {
            StatusMessage = "製品を選択してください。";
            return;
        }

        SelectFolder(folder =>
        {
            if (!SelectedProductKnowledge.ManualFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                SelectedProductKnowledge.ManualFolders.Add(folder);
                SelectedManualFolderPath = folder;
                ManualFolder = folder;
                RefreshProductContextComputedProperties();
            }
        });
    }

    private void RemoveProductManualFolder()
    {
        if (SelectedProductKnowledge is null || string.IsNullOrWhiteSpace(SelectedManualFolderPath))
        {
            StatusMessage = "削除するマニュアルフォルダを選択してください。";
            return;
        }

        SelectedProductKnowledge.ManualFolders.Remove(SelectedManualFolderPath);
        SelectedManualFolderPath = SelectedProductKnowledge.ManualFolders.FirstOrDefault() ?? string.Empty;
        ManualFolder = SelectedManualFolderPath;
        RefreshProductContextComputedProperties();
    }

    private void AddProductDocumentUrl()
    {
        if (SelectedProductKnowledge is null)
        {
            StatusMessage = "製品を選択してください。";
            return;
        }

        var url = NewDocumentUrl.Trim();
        if (!IsHttpOrHttpsUrl(url))
        {
            StatusMessage = "URLは http または https の形式で入力してください。";
            return;
        }

        if (!SelectedProductKnowledge.DocumentUrls.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            SelectedProductKnowledge.DocumentUrls.Add(url);
            SelectedDocumentUrl = url;
            RefreshProductContextComputedProperties();
            ScheduleAutoSave();
        }

        NewDocumentUrl = string.Empty;
    }

    private void RemoveProductDocumentUrl()
    {
        if (SelectedProductKnowledge is null || string.IsNullOrWhiteSpace(SelectedDocumentUrl))
        {
            StatusMessage = "削除するURLを選択してください。";
            return;
        }

        SelectedProductKnowledge.DocumentUrls.Remove(SelectedDocumentUrl);
        SelectedDocumentUrl = SelectedProductKnowledge.DocumentUrls.FirstOrDefault() ?? string.Empty;
        RefreshProductContextComputedProperties();
        ScheduleAutoSave();
    }

    private void UseSelectedProduct()
    {
        if (SelectedProductKnowledge is null)
        {
            StatusMessage = "製品を選択してください。";
            return;
        }

        ApplySelectedProductToCurrentFields();
        RefreshProductContextComputedProperties();
        ProductKnowledgeStatusText = $"現在の検索対象: {SelectedProductKnowledge.ProductName}";
        StatusMessage = ProductKnowledgeStatusText;
    }

    private async Task CheckOllamaConnectionAsync()
    {
        await RunBusyAsync(async () =>
        {
            var models = await ollamaConnectionChecker.ListModelsAsync(BuildSettings().LlmProvider);
            if (!await ResolveAndApplyAvailableModelAsync(models, persist: true))
            {
                StatusMessage = "Ollama接続確認を中止しました。回答モデルを解決できません。";
                return;
            }

            var settings = BuildSettings();
            var result = await ollamaConnectionChecker.CheckAsync(settings.LlmProvider, settings.DisableThinking);
            ReplaceAvailableModels(result.AvailableModels);
            OllamaConnectionResultText = FormatOllamaConnectionResult(result);
            UpdateModelRecommendationText();

            var logger = loggerFactory(EffectiveAiDataFolder());
            if (result.IsSuccess && result.SelectedModelExists)
            {
                await logger.LogInfoAsync($"Ollama connection succeeded. Endpoint={result.Endpoint}; Models={result.AvailableModels.Count}; SelectedModel={result.SelectedModel}");
            }
            else if (result.IsSuccess)
            {
                await logger.LogWarningAsync($"Ollama connection succeeded but selected model was not found. Endpoint={result.Endpoint}; Models={result.AvailableModels.Count}; SelectedModel={result.SelectedModel}");
            }
            else
            {
                await logger.LogErrorAsync($"Ollama connection failed. Endpoint={result.Endpoint}; ErrorCode={result.ErrorCode}; Message={result.Message}");
            }

            StatusMessage = result.IsSuccess ? "Ollama接続確認が完了しました。" : "Ollama接続確認に失敗しました。";
            LastOperationResult = result.Message;
        });
    }

    private async Task RunOllamaProductionMiniTestAsync()
    {
        await RunBusyAsync(async () =>
        {
            var settings = BuildSettings();
            var providerSettings = BuildOllamaProductionMiniTestProviderSettings(settings.LlmProvider);
            var promptMessages = BuildOllamaProductionMiniTestPromptMessages();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var generation = await llmClientFactory
                    .Create(providerSettings)
                    .GenerateAsync(promptMessages, providerSettings, disableThinking: true);
                stopwatch.Stop();

                OllamaProductionMiniTestResultText = FormatOllamaProductionMiniTestResult(
                    isSuccess: true,
                    providerSettings,
                    promptMessages,
                    generation,
                    stopwatch.Elapsed,
                    error: null);
                GenerationDiagnosticsText = OllamaProductionMiniTestResultText;
                StatusMessage = "Ollama本番生成ミニテストが完了しました。";
                LastOperationResult = "Ollama production mini test succeeded.";
                await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                    $"Ollama production mini test succeeded. Model={providerSettings.ChatModel}; TimeoutSeconds={providerSettings.TimeoutSeconds}; ElapsedSeconds={stopwatch.Elapsed.TotalSeconds:0.0}; PromptChars={promptMessages.Diagnostics.FinalPromptChars}; Evidence=0; ThinkFalse=yes; ContentReturned={generation.ContentReturned}; ThinkingReturned={generation.ThinkingReturned}; DoneReason={generation.DoneReason ?? "(unset)"}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                OllamaProductionMiniTestResultText = FormatOllamaProductionMiniTestResult(
                    isSuccess: false,
                    providerSettings,
                    promptMessages,
                    null,
                    stopwatch.Elapsed,
                    ex);
                GenerationDiagnosticsText = OllamaProductionMiniTestResultText;
                ErrorText = FormatExceptionForUi(ex);
                StatusMessage = $"Ollama本番生成ミニテストに失敗しました: {ErrorText}";
                LastOperationResult = $"Ollama production mini test failed. Error={ex.GetType().Name}";
                await loggerFactory(EffectiveAiDataFolder()).LogErrorAsync(
                    $"Ollama production mini test failed. Model={providerSettings.ChatModel}; TimeoutSeconds={providerSettings.TimeoutSeconds}; ElapsedSeconds={stopwatch.Elapsed.TotalSeconds:0.0}; PromptChars={promptMessages.Diagnostics.FinalPromptChars}; Evidence=0; ThinkFalse=yes",
                    ex);
            }
        });
    }

    private async Task RefreshOllamaModelsAsync()
    {
        await RunBusyAsync(RefreshOllamaModelsCoreAsync);
    }

    private async Task RefreshOllamaModelsCoreAsync()
    {
        var models = await ollamaConnectionChecker.ListModelsAsync(BuildSettings().LlmProvider);
        ReplaceAvailableModels(models);
        _ = await ResolveAndApplyAvailableModelAsync(models, persist: true);
    }

    private async Task RunModelCompatibilityTestAsync()
    {
        await RunBusyAsync(async () =>
        {
            var settings = BuildSettings();
            var provider = settings.LlmProvider;
            const string expected = "接続確認に成功しました。";
            var messages = new PromptMessages
            {
                SystemPrompt = "日本語で指定された一文だけを回答してください。",
                UserPrompt = $"日本語で「{expected}」と一文だけ回答してください。",
            };
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var generation = await llmClientFactory.Create(provider).GenerateAsync(
                    messages,
                    provider,
                    DisableThinking);
                stopwatch.Stop();
                var profileUpdated = generation.Diagnostics.Any(diagnostic =>
                    diagnostic.Contains("Unsupported Ollama parameter", StringComparison.OrdinalIgnoreCase));
                if (profileUpdated)
                {
                    RecordCompatiblePlainTextProfile(provider.ChatModel);
                    await settingsStore.SaveAsync(BuildSettings());
                }

                var profile = ModelCapabilityProfiles.Resolve(provider.ChatModel, modelCapabilityProfiles);
                var durationSeconds = generation.TotalDuration is > 0
                    ? generation.TotalDuration.Value / 1_000_000_000d
                    : stopwatch.Elapsed.TotalSeconds;
                var tokensPerSecond = generation.EvalCount is > 0 && durationSeconds > 0
                    ? generation.EvalCount.Value / durationSeconds
                    : 0;
                ModelCompatibilityTestResultText = string.Join(Environment.NewLine,
                [
                    $"Model: {provider.ChatModel}",
                    $"Available: {AvailableModels.Any(model => ModelNameMatches(model, provider.ChatModel))}",
                    $"Chat success: {generation.Content.Contains(expected, StringComparison.Ordinal)}",
                    $"送信したthinking設定: {profile.ThinkingParameterType} {profile.ThinkingValue}".TrimEnd(),
                    $"Content returned: {generation.ContentReturned}",
                    $"Thinking returned: {generation.ThinkingReturned}",
                    $"Done reason: {generation.DoneReason ?? "(未設定)"}",
                    $"Elapsed seconds: {stopwatch.Elapsed.TotalSeconds:0.0}",
                    $"Tokens per second: {tokensPerSecond:0.0}",
                    $"Profile updated: {profileUpdated}",
                    "Error: (なし)",
                ]);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                ModelCompatibilityTestResultText = string.Join(Environment.NewLine,
                [
                    $"Model: {provider.ChatModel}",
                    $"Available: {AvailableModels.Any(model => ModelNameMatches(model, provider.ChatModel))}",
                    "Chat success: False",
                    $"Elapsed seconds: {stopwatch.Elapsed.TotalSeconds:0.0}",
                    $"Error: {ex.GetType().Name}: {ex.Message}",
                ]);
            }
        });
    }

    private void RecordCompatiblePlainTextProfile(string modelName)
    {
        var current = ModelCapabilityProfiles.Resolve(modelName, modelCapabilityProfiles);
        var updated = current with
        {
            ModelName = modelName,
            ThinkingParameterType = ThinkingParameterTypes.None,
            ThinkingValue = string.Empty,
            StructuredOutputMode = StructuredOutputModes.PlainText,
        };
        modelCapabilityProfiles = modelCapabilityProfiles
            .Where(profile => !ModelNameMatches(profile.ModelName, modelName))
            .Append(updated)
            .OrderBy(static profile => profile.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ReplaceAvailableModels(IReadOnlyList<string> models, bool confirmedByOllama = true)
    {
        var normalizedModels = models
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedModels.Count == 0)
        {
            return;
        }

        AvailableModels.Clear();
        foreach (var model in normalizedModels)
        {
            AvailableModels.Add(model);
        }

        ollamaModelsLoaded |= confirmedByOllama;
    }

    private void ApplyQualityModeProfile(string qualityMode)
    {
        var requestedModel = ModelCapabilityProfiles.ModelForQualityMode(qualityMode);
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return;
        }

        var selectedModel = requestedModel;
        var source = ModelResolutionSources.Preset;
        if (ollamaModelsLoaded && AvailableModels.Count > 0)
        {
            var resolution = OllamaModelResolver.Resolve(null, qualityMode, AvailableModels.ToList());
            selectedModel = resolution.Model;
            source = resolution.Source;
            if (!string.Equals(selectedModel, requestedModel, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = $"品質モードの既定モデル '{requestedModel}' がないため、'{selectedModel}' を使用します。自動pullは行いません。";
            }
        }

        if (!AvailableModels.Any(model => ModelNameMatches(model, selectedModel)))
        {
            ReplaceAvailableModels(
                AvailableModels.Append(selectedModel).ToList(),
                confirmedByOllama: false);
        }

        ChatModel = selectedModel;
        ModelResolutionSource = source;
        ApplyModelProfile(selectedModel);
    }

    private void ApplyModelProfile(string selectedModel)
    {
        var profile = ModelCapabilityProfiles.Resolve(selectedModel, modelCapabilityProfiles);
        Temperature = profile.Temperature;
        MaxOutputTokens = profile.MaxOutputTokens;
        TimeoutSeconds = profile.TimeoutSeconds;
        MaxPromptChars = profile.MaxPromptChars;
        MaxEvidenceItems = profile.RecommendedEvidenceCount;
    }

    private async Task<bool> ResolveAndApplyAvailableModelAsync(
        IReadOnlyList<string> models,
        bool persist)
    {
        var previousModel = ChatModel;
        var resolution = OllamaModelResolver.Resolve(previousModel, AnswerQualityMode, models);
        ModelResolutionSource = resolution.Source;
        if (!resolution.IsResolved)
        {
            ChatModel = string.Empty;
            var list = resolution.AvailableModels.Count == 0
                ? "- (なし)"
                : string.Join(Environment.NewLine, resolution.AvailableModels.Select(static model => $"- {model}"));
            OllamaConnectionResultText = $"回答モデルを解決できませんでした。{Environment.NewLine}利用可能モデル:{Environment.NewLine}{list}";
            GenerationState = "NeedsConfiguration";
            GenerationSkippedReason = "ModelUnresolved";
            UpdateRagDiagnostics();
            return false;
        }

        ChatModel = resolution.Model;
        if (!string.Equals(resolution.Source, ModelResolutionSources.Saved, StringComparison.Ordinal))
        {
            ApplyModelProfile(resolution.Model);
        }

        OllamaConnectionResultText = $"Ollama接続: Ready / モデル数: {models.Count} / 選択: {ChatModel} / Source: {resolution.Source}";
        if (persist && settingsLoaded && !string.Equals(previousModel, ChatModel, StringComparison.OrdinalIgnoreCase))
        {
            await settingsStore.SaveAsync(BuildSettings());
        }

        UpdateRagDiagnostics();
        return true;
    }

    private static bool ModelNameMatches(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.Replace(":latest", string.Empty, StringComparison.OrdinalIgnoreCase), right, StringComparison.OrdinalIgnoreCase)
            || string.Equals(left, right.Replace(":latest", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }

    private static PromptMessages BuildOllamaProductionMiniTestPromptMessages()
    {
        const string systemPrompt = "日本語で短く回答してください。";
        const string userPrompt = "疎通確認です。OKだけ返してください。";
        return new PromptMessages
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Diagnostics = new PromptDiagnostics
            {
                ConfiguredMaxPromptChars = systemPrompt.Length + userPrompt.Length,
                FinalPromptChars = systemPrompt.Length + userPrompt.Length,
                SystemChars = systemPrompt.Length,
                UserPromptChars = userPrompt.Length,
                InquiryChars = userPrompt.Length,
                EvidenceChars = 0,
                EvidenceCount = 0,
            },
        };
    }

    private static LlmProviderSettings BuildOllamaProductionMiniTestProviderSettings(LlmProviderSettings settings)
    {
        var outputTokens = settings.MaxOutputTokens > 0
            ? settings.MaxOutputTokens
            : ProductionMiniTestMaxOutputTokens;
        var timeoutSeconds = settings.TimeoutSeconds > 0
            ? settings.TimeoutSeconds
            : ProductionMiniTestMaxTimeoutSeconds;
        var contextWindowTokens = settings.ContextWindowTokens > 0
            ? settings.ContextWindowTokens
            : ProductionMiniTestMaxContextWindowTokens;

        return settings with
        {
            Provider = "Ollama",
            Temperature = Math.Clamp(settings.Temperature, 0, 0.1),
            MaxOutputTokens = Math.Clamp(
                outputTokens,
                ProductionMiniTestMinOutputTokens,
                ProductionMiniTestMaxOutputTokens),
            TimeoutSeconds = Math.Clamp(
                timeoutSeconds,
                ProductionMiniTestMinTimeoutSeconds,
                ProductionMiniTestMaxTimeoutSeconds),
            ContextWindowTokens = Math.Clamp(
                contextWindowTokens,
                ProductionMiniTestMinContextWindowTokens,
                ProductionMiniTestMaxContextWindowTokens),
        };
    }

    private async Task LoadCaseAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(CaseFolderPath))
            {
                StatusMessage = "案件フォルダを指定してください。";
                return;
            }

            if (!Directory.Exists(CaseFolderPath))
            {
                StatusMessage = "指定された案件フォルダが存在しません。";
                return;
            }

            SetOperationProgress(15, "案件ノートを読み込んでいます");
            currentCaseContext = await caseContextBuilder.BuildFromCaseFolderAsync(
                CaseFolderPath,
                ProductName,
                BaseFolder,
                CloseFolder);
            inquiryManuallyEdited = false;
            ApplyCaseContext(currentCaseContext);
            ApplyPreferredCustomerInquiry(currentCaseContext.Notes);
            SetOperationProgress(55, "過去回答候補を検索しています");
            await RefreshPastAnswerCandidateCoreAsync();
            SetOperationProgress(90, "案件情報を反映しています");
            StatusMessage = "選択された案件フォルダを読み込みました。";
            LastOperationResult = "案件読み込み完了";
        });
    }

    private async Task ReloadNotesAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(CaseFolderPath) || !Directory.Exists(CaseFolderPath))
            {
                StatusMessage = "ノート再読み込みには存在する案件フォルダが必要です。";
                return;
            }

            SetOperationProgress(20, "案件ノートを再読み込みしています");
            var notes = await noteSnapshotReader.ReadAllAsync(CaseFolderPath);
            ReplaceNotes(notes);
            ApplyPreferredCustomerInquiry(notes);
            currentCaseContext = BuildCurrentCaseContext();
            SetOperationProgress(60, "過去回答候補を再検索しています");
            await RefreshPastAnswerCandidateCoreAsync();
            SetOperationProgress(90, "再読み込み結果を反映しています");
            StatusMessage = "ノートを再読み込みしました。";
            LastOperationResult = "ノート再読み込み完了";
        });
    }

    private async Task BuildIndexAsync()
    {
        await RunBusyAsync(async () =>
        {
            var selectedProduct = GetSelectedProductSettings();
            if (selectedProduct is not null)
            {
                if (string.IsNullOrWhiteSpace(selectedProduct.CloseFolder) || !Directory.Exists(selectedProduct.CloseFolder))
                {
                    IndexBuildResultText = "Selected product close folder does not exist.";
                    StatusMessage = "Selected product close folder is not available.";
                    return;
                }

                var productResult = await productScopedIndexService.BuildCaseIndexAsync(selectedProduct, EffectiveAiIndexFolder());
                IndexBuildResultText = FormatIndexBuildResult(productResult, selectedProduct.ProductName, selectedProduct.CloseFolder);
                await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                    $"Product case index built. Product={selectedProduct.ProductName}; Cases={productResult.IndexedCaseCount}; Notes={productResult.IndexedNoteCount}; AnswerPairs={productResult.IndexedAnswerPairCount}; Errors={productResult.ErrorCount}; Path={productResult.IndexFilePath}");
                RefreshProductContextComputedProperties();
                StatusMessage = "Product case index build completed.";
                LastOperationResult = $"Product case index build: {productResult.IndexedNoteCount} notes";
                return;
            }

            if (string.IsNullOrWhiteSpace(CloseFolder) || !Directory.Exists(CloseFolder))
            {
                IndexBuildResultText = "Close folder does not exist.";
                StatusMessage = "Index source folder is not available.";
                return;
            }

            var result = await caseIndexBuilder.BuildAsync(CloseFolder, EffectiveAiIndexFolder());
            IndexBuildResultText = FormatIndexBuildResult(result, ProductName, CloseFolder);
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                $"Index built. Cases={result.IndexedCaseCount}; Notes={result.IndexedNoteCount}; AnswerPairs={result.IndexedAnswerPairCount}; Errors={result.ErrorCount}; Path={result.IndexFilePath}");
            RefreshProductContextComputedProperties();
            StatusMessage = "Index build completed.";
            LastOperationResult = $"Index build: {result.IndexedNoteCount} notes";
        });
    }

    private async Task BuildManualIndexAsync()
    {
        await RunBusyAsync(async () =>
        {
            var selectedProduct = GetSelectedProductSettings();
            if (selectedProduct is not null)
            {
                if (selectedProduct.ManualFolders.Count == 0)
                {
                    ManualIndexBuildResultText = "Selected product has no manual folders.";
                    StatusMessage = "Selected product manual folders are not configured.";
                    return;
                }

                var productResult = await productScopedIndexService.BuildManualIndexAsync(selectedProduct, EffectiveAiIndexFolder());
                ManualIndexBuildResultText = FormatManualIndexBuildResult(productResult, selectedProduct.ProductName, selectedProduct.ManualFolders);
                await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                    $"Product manual index built. Product={selectedProduct.ProductName}; Files={productResult.IndexedFileCount}; Chunks={productResult.IndexedChunkCount}; Errors={productResult.ErrorCount}; Path={productResult.IndexFilePath}");
                RefreshProductContextComputedProperties();
                StatusMessage = "Product manual index build completed.";
                LastOperationResult = $"Product manual index build: {productResult.IndexedChunkCount} chunks";
                return;
            }

            if (string.IsNullOrWhiteSpace(ManualFolder) || !Directory.Exists(ManualFolder))
            {
                ManualIndexBuildResultText = "Manual folder does not exist.";
                StatusMessage = "Manual index source folder is not available.";
                return;
            }

            var result = await manualIndexBuilder.BuildAsync(ManualFolder, EffectiveAiIndexFolder());
            ManualIndexBuildResultText = FormatManualIndexBuildResult(result, ProductName, [ManualFolder]);
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                $"Manual index built. Files={result.IndexedFileCount}; Chunks={result.IndexedChunkCount}; Errors={result.ErrorCount}; Path={result.IndexFilePath}");
            RefreshProductContextComputedProperties();
            StatusMessage = "Manual index build completed.";
            LastOperationResult = $"Manual index build: {result.IndexedChunkCount} chunks";
        });
    }

    private async Task BuildOfficialDocumentIndexAsync()
    {
        await RunBusyAsync(async () =>
        {
            var selectedProduct = GetSelectedProductSettings();
            if (selectedProduct is null)
            {
                OfficialDocumentIndexBuildResultText = "公式URLインデックス作成には製品別設定の選択が必要です。";
                StatusMessage = OfficialDocumentIndexBuildResultText;
                return;
            }

            if (selectedProduct.DocumentUrls.Count == 0)
            {
                OfficialDocumentIndexBuildResultText = "選択製品に公式URLが登録されていません。";
                StatusMessage = "公式URLが未登録です。";
                return;
            }

            var result = await productScopedIndexService.BuildOfficialDocumentIndexAsync(
                selectedProduct,
                EffectiveAiIndexFolder());
            OfficialDocumentIndexBuildResultText = FormatOfficialDocumentIndexBuildResult(result);
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                $"Official document index built. Product={selectedProduct.ProductName}; Urls={result.SourceUrlCount}; Success={result.FetchSuccessCount}; Failures={result.FetchFailureCount}; Chunks={result.IndexedChunkCount}; Path={result.IndexFilePath}");
            RefreshProductContextComputedProperties();
            StatusMessage = "公式URLインデックス作成が完了しました。";
            LastOperationResult = $"Official document index build: {result.IndexedChunkCount} chunks";
        });
    }

    private async Task SearchPastCasesAsync()
    {
        await RunCombinedSearchAsync("過去案件検索");
    }

    private async Task SearchManualsAsync()
    {
        await RunCombinedSearchAsync("マニュアル検索");
    }

    private async Task RunCombinedSearchAsync(string operationName)
    {
        await RunBusyAsync(async () =>
        {
            SetOperationProgress(5, "問い合わせ内容を解析しています");
            GenerationState = "Searching";
            GenerationSkippedReason = string.Empty;
            var searchLimit = Math.Max(12, Math.Max(1, MaxEvidenceItems) * 2);
            var selectedProduct = ResolveProductForSearch();
            allowPastAnswerAutoSelection = selectedProduct is not null;
            lastInquiryFocus = inquiryFocusExtractor.Extract(InquiryText, BuildCurrentCaseContext());
            InquiryFocusSummaryText = FormatInquiryFocusSummary(lastInquiryFocus);
            SetOperationProgress(20, "ナレッジを検索しています");

            if (selectedProduct is null)
            {
                var rootPastCases = await keywordSearcher.SearchAsync(
                    EffectiveAiIndexFolder(),
                    lastInquiryFocus.FocusText,
                    searchLimit);
                var crossProductAnswers = await productScopedSearchService.SearchPastAnswersAcrossProductsAsync(
                    BuildProductKnowledgeSettings(),
                    EffectiveAiIndexFolder(),
                    InquiryText,
                    searchLimit);
                lastSearchSources = crossProductAnswers.Concat(rootPastCases).ToList();
                lastManualSearchSources = await manualKeywordSearcher.SearchAsync(
                    EffectiveAiIndexFolder(),
                    lastInquiryFocus.FocusText,
                    searchLimit);
                lastOfficialDocumentSearchSources = [];
            }
            else
            {
                var allSources = await productScopedSearchService.SearchAllHybridAsync(
                    selectedProduct,
                    EffectiveAiIndexFolder(),
                    lastInquiryFocus,
                    BuildSettings().LlmProvider,
                    searchLimit * 3);
                lastSearchSources = allSources
                    .Where(static source => source.SourceType is "PastCaseNote" or "ExactPastAnswer" or "PastAnswer")
                    .ToList();
                lastManualSearchSources = allSources
                    .Where(static source => string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                lastOfficialDocumentSearchSources = allSources
                    .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!inquiryManuallyEdited && !string.IsNullOrWhiteSpace(SupportNumber))
                {
                    SetOperationProgress(65, "案件番号から過去回答を照合しています");
                    var supportNumberAnswers = await productScopedSearchService.SearchPastAnswersBySupportNumberAsync(
                        selectedProduct,
                        EffectiveAiIndexFolder(),
                        SupportNumber,
                        searchLimit);
                    lastSearchSources = MergeSearchSources(supportNumberAnswers, lastSearchSources, searchLimit * 2);
                }
            }

            SetOperationProgress(80, "検索結果を整理しています");
            var combined = BuildCombinedSearchSources();
            ReplaceSearchResults(combined);
            UpdatePastAnswerCandidate(selectedProduct);
            var appliedSupportNumberAnswer = TryApplySupportNumberPastAnswer();
            SearchResultsText = FormatSearchResults(combined);
            UpdatePromptSummary();
            var summary = SearchSourceSummaryBuilder.Build(
                SearchResults,
                SourceTypeFilter,
                MaxEvidenceItems,
                HighScoreThreshold,
                MinimumDisplayScore,
                lastInquiryFocus.IsFreshnessSensitive,
                EnableTopNFallback);
            UpdateOfficialDocDiagnostics(summary);
            if (!appliedSupportNumberAnswer)
            {
                GenerationState = "Ready";
            }
            UpdateRagDiagnostics();
            SetOperationProgress(95, "検索結果を表示しています");
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                $"Keyword search completed. Operation={operationName}; Product={selectedProduct?.ProductName ?? "(root)"}; PastCaseResults={lastSearchSources.Count}; ManualResults={lastManualSearchSources.Count}; OfficialDocResults={lastOfficialDocumentSearchSources.Count}; FreshnessSensitive={lastInquiryFocus.IsFreshnessSensitive}; CombinedResults={SearchResults.Count}; VisibleResults={summary.FilteredCount}; HiddenBySourceTypeFilter={summary.HiddenBySourceTypeFilterCount}; HiddenByMinimumScore={summary.HiddenByMinimumScoreCount}; BelowAutoSelectScore={summary.BelowAutoSelectScoreCount}; IndexFolder={GetCurrentSearchIndexFolder()}");
            StatusMessage = $"{operationName}が完了しました。";
            LastOperationResult = $"{operationName}: {combined.Count} results";
        });
    }

    private void SelectVisibleSources()
    {
        SearchSourceFiltering.SetVisibleSelection(SearchResults, SourceTypeFilter, isSelected: true, MinimumDisplayScore);
        UpdatePromptSummary();
        StatusMessage = "Selected visible evidence.";
    }

    private void ClearVisibleSources()
    {
        SearchSourceFiltering.SetVisibleSelection(SearchResults, SourceTypeFilter, isSelected: false, MinimumDisplayScore);
        UpdatePromptSummary();
        StatusMessage = "Cleared visible evidence selection.";
    }

    private void SelectHighScoreSources()
    {
        SearchSourceFiltering.SelectHighScoreVisible(SearchResults, SourceTypeFilter, HighScoreThreshold, MinimumDisplayScore);
        UpdatePromptSummary();
        StatusMessage = $"Selected visible evidence with score >= {HighScoreThreshold:0.000}.";
    }

    private void ClearAllSources()
    {
        SearchSourceFiltering.ClearAll(SearchResults);
        UpdatePromptSummary();
        StatusMessage = "Cleared all evidence selection.";
    }

    private void ToggleSelectedSource()
    {
        if (SelectedSearchResult is null)
        {
            StatusMessage = "No search result is selected.";
            return;
        }

        SelectedSearchResult.IsSelected = !SelectedSearchResult.IsSelected;
        UpdatePromptSummary();
        StatusMessage = SelectedSearchResult.IsSelected
            ? "Selected current evidence."
            : "Cleared current evidence selection.";
    }

    private async Task GenerateHighQualityDraftAsync()
    {
        AnswerQualityMode = SupportCaseManager.Ai.Contracts.AnswerQualityModes.Quality;
        await GenerateDraftAsync();
    }

    private async Task GenerateDraftAsync()
    {
        await RunBusyAsync(async () =>
        {
            SetOperationProgress(5, "回答生成の条件を確認しています");
            GenerationState = "Generating";
            GenerationSkippedReason = string.Empty;
            var provider = NormalizeProvider(LlmProvider);
            var model = ChatModel;
            DraftProviderStatusText = FormatDraftProviderStatus(provider, model, usedRealLlm: false, usedEvidenceCount: 0, isSuccess: false);
            if (string.IsNullOrWhiteSpace(InquiryText))
            {
                SkipGeneration("NeedsConfiguration", "InquiryEmpty", "問い合わせ本文が空のため回答生成を開始しませんでした。");
                return;
            }

            var resolvedProduct = ResolveProductForSearch();
            if (resolvedProduct is null)
            {
                SkipGeneration("NeedsConfiguration", "ProductUnresolved", "製品を解決できないため回答生成を開始しませんでした。製品を選択してください。");
                return;
            }

            if (!pastAnswerPolishRequested && TryApplySupportNumberPastAnswer())
            {
                SetOperationProgress(100, "過去回答を表示しました");
                return;
            }

            if (provider == "Ollama" && string.IsNullOrWhiteSpace(model))
            {
                if (selectedPastAnswerCandidate is not null && allowPastAnswerAutoSelection)
                {
                    ApplyPastAnswerCandidate("回答モデル未解決のため、完全一致した過去回答をLLMなしで表示しました。");
                    return;
                }

                SkipGeneration("NeedsConfiguration", "ModelUnresolved", BuildUnresolvedModelMessage());
                return;
            }

            if (!HasKnowledgeForProduct(resolvedProduct) && SearchResults.Count == 0)
            {
                SkipGeneration("NeedsConfiguration", "KnowledgeNotCreated", "ナレッジが未作成のため回答生成を開始しませんでした。ナレッジを更新してください。");
                return;
            }

            SetOperationProgress(25, "回答に使う根拠を選定しています");
            lastRequest = BuildDraftRequest();
            provider = NormalizeProvider(lastRequest.Settings.LlmProvider.Provider);
            model = lastRequest.Settings.LlmProvider.ChatModel;
            lastUsedSources = lastRequest.Sources;
            MarkUsedSources(lastUsedSources);
            UsedSourcesText = FormatUsedSources(lastUsedSources);
            UsedEvidenceCount = lastUsedSources.Count;

            var hasCuratedFacts = lastRequest.FactResolution?.ResolvedFacts.Any(static fact =>
                string.Equals(fact.SourceType, "Curated", StringComparison.OrdinalIgnoreCase)) == true;
            var hasUsablePastAnswer = selectedPastAnswerCandidate is not null && allowPastAnswerAutoSelection;
            if (lastRequest.Settings.SkipGenerationWhenNoEvidence &&
                lastUsedSources.Count == 0 &&
                !hasCuratedFacts &&
                !hasUsablePastAnswer)
            {
                lastResult = BuildNoEvidenceSkippedResult();
                ApplyDraftResult(lastResult);
                GenerationDiagnosticsText = FormatGenerationNoEvidenceSkippedDiagnostics(lastRequest);
                DraftProviderStatusText = FormatDraftProviderStatus(provider, model, usedRealLlm: false, usedEvidenceCount: 0, isSuccess: true);
                WarningsText = PrependWarning(WarningsText, "根拠0件のためLLM呼び出しをスキップしました。検索結果から根拠を選択してください。");
                StatusMessage = "根拠0件のため回答生成をスキップしました。";
                LastOperationResult = $"Draft generation skipped. Reason=NoEvidence; Provider={provider}; Model={model}; Evidence=0";
                GenerationState = "NoEvidence";
                GenerationSkippedReason = "NoEvidence";
                UpdateRagDiagnostics();
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                    $"Draft generation skipped. Reason=NoEvidence; Provider={provider}; Model={model}; Evidence=0");
                return;
            }

            if (ShouldSkipFreshnessWithoutOfficialDoc(lastRequest))
            {
                lastResult = BuildFreshnessNoOfficialDocResult(lastRequest);
                ApplyDraftResult(lastResult);
                GenerationDiagnosticsText = FormatGenerationSkippedDiagnostics(lastRequest);
                DraftProviderStatusText = FormatDraftProviderStatus(provider, model, usedRealLlm: false, usedEvidenceCount: lastUsedSources.Count, isSuccess: true);
                WarningsText = PrependWarning(WarningsText, "鮮度重要な問い合わせですが、OfficialDoc根拠がないためLLM呼び出しをスキップしました。");
                StatusMessage = "OfficialDoc根拠がない鮮度重要問い合わせのため、安全な固定回答案を表示しました。";
                LastOperationResult = $"Draft generation skipped. Reason=FreshnessWithoutOfficialDoc; Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}";
                GenerationState = "Completed";
                GenerationSkippedReason = "FreshnessWithoutOfficialDoc";
                UpdateRagDiagnostics();
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                    $"Draft generation skipped. Reason=FreshnessWithoutOfficialDoc; Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}; OfficialDoc=0");
                return;
            }

            generationCancellation?.Dispose();
            generationCancellation = new CancellationTokenSource();
            CancelGenerationCommand.RaiseCanExecuteChanged();
            try
            {
                var routeDescription = !string.Equals(model, ChatModel, StringComparison.OrdinalIgnoreCase)
                    ? $"高速モデル {model} でLLM回答を待っています"
                    : "LLMからの回答を待っています";
                SetOperationProgress(45, routeDescription);
                lastResult = await GenerateDraftWithProgressAsync(lastRequest, routeDescription, generationCancellation.Token);
            }
            catch (OperationCanceledException) when (generationCancellation.IsCancellationRequested)
            {
                GenerationDiagnosticsText = $"回答生成はユーザー操作で中止されました。{Environment.NewLine}使用モデル: {model}";
                ErrorText = string.Empty;
                DraftProviderStatusText = FormatDraftProviderStatus(provider, model, provider == "Ollama", lastUsedSources.Count, isSuccess: false);
                StatusMessage = "回答生成を中止しました。根拠の検索結果は保持しています。";
                LastOperationResult = $"Draft generation canceled. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}";
                GenerationState = "Canceled";
                GenerationSkippedReason = "CanceledByUser";
                SetOperationProgress(100, "中止");
                UpdateRagDiagnostics();
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                    $"Draft generation canceled. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}");
                return;
            }
            catch (Exception ex) when (CanBuildManualTimeoutFallback(lastRequest, ex))
            {
                lastResult = BuildManualTimeoutFallbackResult(lastRequest, ex);
                ApplyDraftResult(lastResult);
                GenerationDiagnosticsText = FormatGenerationFailureDiagnostics(lastRequest, ex)
                    + Environment.NewLine
                    + "LLMタイムアウトのため、送信済みのマニュアル根拠を回答案として表示しました。";
                ErrorText = FormatExceptionForUi(ex);
                DraftProviderStatusText = FormatDraftProviderStatus(provider, model, provider == "Ollama", lastUsedSources.Count, isSuccess: false);
                StatusMessage = "LLMが時間内に完了しなかったため、PDFマニュアルの該当根拠を回答案として表示しました。";
                LastOperationResult = $"Draft generation completed with manual fallback. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}; Error={ex.GetType().Name}";
                GenerationState = "CompletedWithFallback";
                GenerationSkippedReason = "LlmTimeoutManualFallback";
                SetOperationProgress(100, "マニュアル根拠を表示しました");
                UpdateRagDiagnostics();
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                    $"Draft generation timed out; manual fallback displayed. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}");
                return;
            }
            catch (Exception ex)
            {
                GenerationDiagnosticsText = FormatGenerationFailureDiagnostics(lastRequest, ex);
                ErrorText = FormatExceptionForUi(ex);
                if (!string.IsNullOrWhiteSpace(GenerationDiagnosticsText))
                {
                    ErrorText = $"{ErrorText}{Environment.NewLine}{GenerationDiagnosticsText}";
                }

                DraftProviderStatusText = FormatDraftProviderStatus(provider, model, provider == "Ollama", lastUsedSources.Count, isSuccess: false);
                StatusMessage = provider == "Ollama"
                    ? $"Ollamaでの回答生成に失敗しました: {ErrorText}"
                    : $"回答生成に失敗しました: {ErrorText}";
                LastOperationResult = $"Draft generation failed. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}; Error={ex.GetType().Name}";
                GenerationState = "Failed";
                GenerationSkippedReason = ex.GetType().Name;
                SetOperationProgress(100, "失敗");
                UpdateRagDiagnostics();
                await loggerFactory(EffectiveAiDataFolder()).LogErrorAsync($"Draft generation failed. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}", ex);
                return;
            }
            finally
            {
                generationCancellation.Dispose();
                generationCancellation = null;
                CancelGenerationCommand.RaiseCanExecuteChanged();
            }

            SetOperationProgress(90, "回答案を整形しています");
            ApplyDraftResult(lastResult);
            GenerationDiagnosticsText = FormatGenerationSuccessDiagnostics(lastRequest, lastResult);
            var completedWithFallback = lastResult.Warnings.Any(static warning =>
                warning.Contains("JSON解析に失敗", StringComparison.Ordinal) ||
                warning.Contains("応答を解析できなかった", StringComparison.Ordinal) ||
                warning.Contains("根拠から回答案を補完", StringComparison.Ordinal) ||
                warning.Contains("根拠からValidateアップロード手順を補完", StringComparison.Ordinal));
            if (lastUsedSources.Count == 0)
            {
                WarningsText = PrependWarning(WarningsText, "LLMへ送信された根拠がありません。");
            }

            DraftProviderStatusText = FormatDraftProviderStatus(provider, model, provider == "Ollama", lastUsedSources.Count, isSuccess: true);
            if (completedWithFallback)
            {
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync($"Draft response parse failed; grounded fallback displayed. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}");
                StatusMessage = "LLM回答をそのまま使用できなかったため、送信済み根拠から回答案を補完しました。";
                LastOperationResult = $"Draft generation completed with grounded fallback. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}";
                GenerationState = "CompletedWithFallback";
                GenerationSkippedReason = "LlmResponseParseFallback";
                SetOperationProgress(98, "根拠から補完した回答案を表示しています");
            }
            else
            {
                await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync($"Draft generated. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}");
                StatusMessage = provider == "Ollama"
                    ? "Ollamaで回答案を生成しました。"
                    : "モック回答案を生成しました。";
                LastOperationResult = $"Draft generated. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}";
                GenerationState = "Completed";
                SetOperationProgress(98, "回答案を表示しています");
            }
            UpdateRagDiagnostics();
        });
    }

    private async Task<AnswerDraftResult> GenerateDraftWithProgressAsync(
        AnswerDraftRequest request,
        string stage,
        CancellationToken cancellationToken)
    {
        var generationTask = answerServiceFactory(request.Settings.LlmProvider)
            .GenerateDraftAsync(request, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var timeoutSeconds = Math.Max(1, request.Settings.LlmProvider.TimeoutSeconds);

        try
        {
            while (!generationTask.IsCompleted)
            {
                var delayTask = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (await Task.WhenAny(generationTask, delayTask) == generationTask)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var elapsedSeconds = Math.Max(1, (int)stopwatch.Elapsed.TotalSeconds);
                var progress = 45 + Math.Min(43, (int)Math.Floor(elapsedSeconds / (double)timeoutSeconds * 43));
                SetOperationProgress(progress, $"{stage} ({elapsedSeconds}秒 / 上限{timeoutSeconds}秒)");
            }

            return await generationTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await generationTask;
            }
            catch
            {
                // Observe the canceled provider task before returning control to the UI.
            }

            throw;
        }
    }

    private void CancelGeneration()
    {
        if (generationCancellation is not { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        SetOperationProgress(OperationProgressPercent, "回答生成を中止しています");
        StatusMessage = "回答生成の中止を要求しました。";
        cancellation.Cancel();
        CancelGenerationCommand.RaiseCanExecuteChanged();
    }

    private void ClearInquiry()
    {
        InquiryText = string.Empty;
        AdditionalInstruction = string.Empty;
        StatusMessage = "問い合わせ入力をクリアしました。";
    }

    private async Task SaveDraftAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (lastRequest is null || lastResult is null)
            {
                StatusMessage = "保存する回答案がありません。先に回答案生成を実行してください。";
                return;
            }

            SavedDraftPath = await draftStore.SaveAsync(lastRequest, lastResult);
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync($"Draft saved: {SavedDraftPath}");
            StatusMessage = "ドラフトをAI専用領域に保存しました。";
            LastOperationResult = "ドラフト保存完了";
        });
    }

    private async Task WriteTestLogAsync()
    {
        await RunBusyAsync(async () =>
        {
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync("Test log from AI assistant GUI skeleton.");
            StatusMessage = "テストログを出力しました。";
            LastOperationResult = "テストログ出力完了";
            OnPropertyChanged(nameof(LogFilePath));
        });
    }

    private void OpenLog()
    {
        if (!File.Exists(LogFilePath))
        {
            StatusMessage = "ログファイルはまだ作成されていません。";
            return;
        }

        Process.Start(new ProcessStartInfo(LogFilePath)
        {
            UseShellExecute = true,
        });
    }

    private async Task RunBusyAsync(Func<Task> action, bool clearExistingError = true)
    {
        var completed = false;
        try
        {
            IsBusy = true;
            SetOperationProgress(0, "処理を開始しています");
            if (clearExistingError)
            {
                ErrorText = string.Empty;
            }

            await action();
            completed = true;
        }
        catch (Exception ex)
        {
            try
            {
                await loggerFactory(EffectiveAiDataFolder()).LogErrorAsync("AI assistant operation failed.", ex);
            }
            catch
            {
                // Keep UI error reporting independent from diagnostic log failures.
            }

            ErrorText = FormatExceptionForUi(ex);
            StatusMessage = $"処理中にエラーが発生しました: {ErrorText}";
            LastOperationResult = $"Error: {ex.GetType().Name}";
            SetOperationProgress(100, "失敗");
        }
        finally
        {
            IsBusy = false;
            if (completed
                && !string.Equals(OperationStage, "失敗", StringComparison.Ordinal)
                && !string.Equals(OperationStage, "中止", StringComparison.Ordinal))
            {
                SetOperationProgress(100, "完了");
            }
        }
    }

    private void SetOperationProgress(int percent, string stage)
    {
        OperationStage = string.IsNullOrWhiteSpace(stage) ? "処理中" : stage.Trim();
        OperationProgressPercent = percent;
    }

    private void SetInquiryTextInternally(string value, bool isExplicitUserInput = false)
    {
        isSettingInquiryInternally = true;
        try
        {
            InquiryText = value;
        }
        finally
        {
            isSettingInquiryInternally = false;
        }

        inquiryManuallyEdited = isExplicitUserInput;
    }

    private void ApplySettings(AiAssistantSettings settings)
    {
        isApplyingSettings = true;
        try
        {
        AiDataFolder = string.IsNullOrWhiteSpace(settings.AiDataFolder)
            ? DefaultAiDataFolder()
            : settings.AiDataFolder;
        AiIndexFolder = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? DefaultAiIndexFolder()
            : settings.AiIndexFolder;
        BaseFolder = settings.BaseFolder ?? string.Empty;
        CloseFolder = settings.CloseFolder ?? string.Empty;
        ManualFolder = settings.ManualFolder ?? string.Empty;
        SupportToolSettingsFilePath = settings.SupportToolSettingsFilePath ?? string.Empty;
        ReplaceProducts(settings);
        ProductName = settings.DefaultProductName ?? ProductName;
        SelectConfiguredProduct(settings.SelectedProductName ?? settings.DefaultProductName);
        UiLanguage = string.IsNullOrWhiteSpace(settings.UiLanguage) ? "ja-JP" : settings.UiLanguage;
        UseDarkMode = settings.UseDarkMode;
        MaxEvidenceItems = settings.MaxEvidenceItems;
        HighScoreThreshold = settings.AutoSelectMinimumScore;
        MinimumDisplayScore = settings.MinimumDisplayScore;
        MaxPromptChars = settings.MaxPromptChars;
        EnableCloudLlm = settings.EnableCloudLlm;
        MaskSensitiveDataForCloud = settings.MaskSensitiveDataForCloud;
        DisableThinking = settings.DisableThinking;
        SkipGenerationWhenNoEvidence = settings.SkipGenerationWhenNoEvidence;
        EnableTopNFallback = settings.EnableTopNFallback;
        LlmProvider = string.IsNullOrWhiteSpace(settings.LlmProvider.Provider) ? "Fake" : settings.LlmProvider.Provider;
        OllamaEndpoint = settings.LlmProvider.Endpoint;
        ChatModel = settings.LlmProvider.ChatModel;
        ModelResolutionSource = string.IsNullOrWhiteSpace(ChatModel)
            ? ModelResolutionSources.Unresolved
            : ModelResolutionSources.Saved;
        ollamaModelsLoaded = false;
        ReplaceAvailableModels(string.IsNullOrWhiteSpace(settings.LlmProvider.ChatModel)
            ? []
            : [settings.LlmProvider.ChatModel], confirmedByOllama: false);
        EmbeddingModel = settings.LlmProvider.EmbeddingModel ?? string.Empty;
        Temperature = settings.LlmProvider.Temperature;
        MaxOutputTokens = settings.LlmProvider.MaxOutputTokens;
        ContextWindowTokens = settings.LlmProvider.ContextWindowTokens;
        TimeoutSeconds = settings.LlmProvider.TimeoutSeconds;
        answerQualityMode = string.IsNullOrWhiteSpace(settings.AnswerQualityMode)
            ? SupportCaseManager.Ai.Contracts.AnswerQualityModes.Custom
            : settings.AnswerQualityMode;
        OnPropertyChanged(nameof(AnswerQualityMode));
        modelCapabilityProfiles = settings.ModelCapabilityProfiles.Count > 0
            ? settings.ModelCapabilityProfiles
            : ModelCapabilityProfiles.GetDefaults();
        CodexExecutablePath = settings.CodexExecutablePath;
        RefreshProductContextComputedProperties();
        }
        finally
        {
            isApplyingSettings = false;
        }
    }

    private async Task ResetSettingsAsync()
    {
        await RunBusyAsync(async () =>
        {
            var defaults = new AiAssistantSettings
            {
                AiDataFolder = DefaultAiDataFolder(),
                AiIndexFolder = DefaultAiIndexFolder(),
                ModelCapabilityProfiles = ModelCapabilityProfiles.GetDefaults(),
            };
            ApplySettings(defaults);
            settingsLoaded = true;
            await settingsStore.SaveAsync(BuildSettings());
            await RefreshKnowledgeStatusCoreAsync();
            StatusMessage = "設定を初期値に戻しました。";
        });
    }

    public async Task FlushSettingsAsync()
    {
        if (!settingsLoaded)
        {
            return;
        }

        autoSaveCancellation?.Cancel();
        await settingsStore.SaveAsync(BuildSettings());
    }

    private async Task RefreshKnowledgeStatusCoreAsync()
    {
        var product = GetSelectedProductSettings();
        if (product is null)
        {
            KnowledgeStatusText = "ナレッジ: 未作成（製品未選択）";
            return;
        }

        var status = await productScopedIndexService.InspectKnowledgeAsync(
            product,
            EffectiveAiIndexFolder());
        KnowledgeStatusText = FormatKnowledgeStatus(status);
        ProductKnowledgeStatusText = KnowledgeStatusText;
    }

    private async Task UpdateKnowledgeAsync(KnowledgeUpdateScope scope, bool forceRebuild)
    {
        await RunBusyAsync(async () =>
        {
            var product = GetSelectedProductSettings();
            if (product is null)
            {
                KnowledgeStatusText = "ナレッジ: 未作成（製品未選択）";
                return;
            }

            KnowledgeStatusText = $"{product.ProductName} ナレッジ: 更新中";
            var result = await productScopedIndexService.UpdateKnowledgeWithEmbeddingsAsync(
                product,
                EffectiveAiIndexFolder(),
                scope,
                forceRebuild,
                EmbeddingModel,
                OllamaEndpoint);
            KnowledgeStatusText = FormatKnowledgeStatus(result.Status);
            ProductKnowledgeStatusText = KnowledgeStatusText;
            LastOperationResult = result.Status.Message;
            StatusMessage = result.Status.Status == KnowledgeStatuses.Ready
                ? "ナレッジ更新が完了しました。"
                : "ナレッジ更新結果を確認してください。";
            RefreshProductContextComputedProperties();
        });
    }

    private static string FormatKnowledgeStatus(KnowledgeIndexStatus status)
    {
        var updated = status.LastUpdatedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "-";
        var statusLabel = status.Status switch
        {
            KnowledgeStatuses.Ready => "Ready",
            KnowledgeStatuses.UpdateAvailable => "更新あり",
            KnowledgeStatuses.Updating => "更新中",
            KnowledgeStatuses.Warning => "警告",
            KnowledgeStatuses.Error => "エラー",
            KnowledgeStatuses.NotCreated => "未作成",
            _ => status.Status,
        };
        return string.Join(Environment.NewLine,
        [
            $"{status.ProductName} ナレッジ: {statusLabel}",
            $"最終更新: {updated}",
            $"Manual: {status.ManualDocumentCount}ファイル / {status.ManualChunkCount}チャンク",
            $"OfficialDoc: {status.OfficialDocumentCount}ページ / {status.OfficialChunkCount}チャンク",
            $"PastCase: {status.PastCaseCount}案件 / {status.PastCaseChunkCount}チャンク",
            status.Message,
        ]);
    }

    private void StartLocalKnowledgeRefreshInBackground()
    {
        var product = GetSelectedProductSettings();
        if (product is null)
        {
            return;
        }

        var indexFolder = EffectiveAiIndexFolder();
        var embedding = EmbeddingModel;
        var embeddingEndpoint = OllamaEndpoint;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await productScopedIndexService.UpdateKnowledgeWithEmbeddingsAsync(
                    product,
                    indexFolder,
                    KnowledgeUpdateScope.PastCases | KnowledgeUpdateScope.Manuals,
                    forceRebuild: false,
                    embeddingModel: embedding,
                    embeddingEndpoint: embeddingEndpoint);
                await ApplyBackgroundKnowledgeStatusAsync(result.Status);
            }
            catch (Exception ex)
            {
                try
                {
                    await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                        $"Background knowledge refresh failed. {ex.GetType().Name}: {ex.Message}");
                }
                catch
                {
                }
            }
        });
    }

    private async Task ApplyBackgroundKnowledgeStatusAsync(KnowledgeIndexStatus status)
    {
        void Apply()
        {
            KnowledgeStatusText = FormatKnowledgeStatus(status);
            ProductKnowledgeStatusText = KnowledgeStatusText;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(Apply);
            return;
        }

        Apply();
    }

    private AiAssistantSettings BuildSettings()
    {
        SynchronizeSelectedProductFromCurrentFields();
        var profile = ModelCapabilityProfiles.Resolve(ChatModel, modelCapabilityProfiles);
        return new AiAssistantSettings
        {
            AiDataFolder = EffectiveAiDataFolder(),
            AiIndexFolder = EffectiveAiIndexFolder(),
            BaseFolder = string.IsNullOrWhiteSpace(BaseFolder) ? null : BaseFolder,
            CloseFolder = string.IsNullOrWhiteSpace(CloseFolder) ? null : CloseFolder,
            ManualFolder = string.IsNullOrWhiteSpace(ManualFolder) ? null : ManualFolder,
            DefaultProductName = string.IsNullOrWhiteSpace(ProductName) ? null : ProductName,
            Products = BuildProductKnowledgeSettings(),
            SupportToolSettingsFilePath = string.IsNullOrWhiteSpace(SupportToolSettingsFilePath) ? null : SupportToolSettingsFilePath,
            SelectedProductName = SelectedProductKnowledge?.ProductName ?? (string.IsNullOrWhiteSpace(ProductName) ? null : ProductName),
            UiLanguage = UiLanguage,
            UseDarkMode = UseDarkMode,
            MaxEvidenceItems = MaxEvidenceItems,
            AutoSelectMinimumScore = HighScoreThreshold,
            MinimumDisplayScore = MinimumDisplayScore,
            MaxPromptChars = MaxPromptChars,
            EnableCloudLlm = EnableCloudLlm,
            MaskSensitiveDataForCloud = MaskSensitiveDataForCloud,
            DisableThinking = DisableThinking,
            SkipGenerationWhenNoEvidence = SkipGenerationWhenNoEvidence,
            EnableTopNFallback = EnableTopNFallback,
            AnswerQualityMode = AnswerQualityMode,
            ModelCapabilityProfiles = modelCapabilityProfiles.Count > 0
                ? modelCapabilityProfiles
                : ModelCapabilityProfiles.GetDefaults(),
            CodexExecutablePath = CodexExecutablePath,
            LlmProvider = new LlmProviderSettings
            {
                Provider = string.IsNullOrWhiteSpace(this.LlmProvider) ? "Fake" : this.LlmProvider,
                Endpoint = OllamaEndpoint,
                ChatModel = ChatModel,
                EmbeddingModel = string.IsNullOrWhiteSpace(EmbeddingModel) ? null : EmbeddingModel,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens,
                ContextWindowTokens = ContextWindowTokens,
                TimeoutSeconds = TimeoutSeconds,
                ThinkingParameterType = profile.ThinkingParameterType,
                ThinkingValue = profile.ThinkingValue,
                StructuredOutputMode = profile.StructuredOutputMode,
            },
        };
    }

    private void ApplyCaseContext(CaseContext context)
    {
        CaseFolderPath = context.CaseFolderPath ?? CaseFolderPath;
        ProductName = context.ProductName ?? ProductName;
        BaseFolder = context.BaseFolder ?? BaseFolder;
        CloseFolder = context.CloseFolder ?? CloseFolder;
        CompanyName = context.CompanyName ?? string.Empty;
        CustomerName = context.CustomerName ?? string.Empty;
        SupportNumber = context.SupportNumber ?? string.Empty;
        Status = context.Status ?? string.Empty;
        ReceptionDate = context.ReceptionDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        ReplaceNotes(context.Notes);
    }

    private void ApplyPreferredCustomerInquiry(IReadOnlyList<NoteSnapshot> notes)
    {
        if (inquiryManuallyEdited)
        {
            return;
        }

        var inquiryNote = notes
            .Where(IsCustomerInquiryNote)
            .OrderByDescending(note => !string.IsNullOrWhiteSpace(SupportNumber)
                && note.FileName.Contains(SupportNumber, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static note => note.LastModifiedAt)
            .FirstOrDefault();
        if (inquiryNote is null || string.IsNullOrWhiteSpace(inquiryNote.Text))
        {
            return;
        }

        SetInquiryTextInternally(inquiryNote.Text);
        SelectedNote = inquiryNote;
    }

    private static bool IsCustomerInquiryNote(NoteSnapshot note)
    {
        return string.Equals(note.NoteKind, "お客様ご相談内容", StringComparison.OrdinalIgnoreCase)
            || note.FileName.StartsWith("お客様ご相談内容_", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyLaunchContextNote(AiAssistantLaunchContext context)
    {
        if (string.IsNullOrWhiteSpace(context.CurrentNoteText)
            && string.IsNullOrWhiteSpace(context.NoteKind)
            && string.IsNullOrWhiteSpace(context.NoteFilePath))
        {
            return;
        }

        var fileName = string.IsNullOrWhiteSpace(context.NoteFilePath)
            ? "launch-context-note.txt"
            : Path.GetFileName(context.NoteFilePath);

        ReplaceNotes(
        [
            new NoteSnapshot
            {
                NoteKind = context.NoteKind,
                FilePath = context.NoteFilePath,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "launch-context-note.txt" : fileName,
                Text = context.CurrentNoteText,
                IsCurrent = true,
            },
        ]);
    }

    private void ReplaceProducts(AiAssistantSettings settings)
    {
        Products.Clear();
        var products = settings.Products
            .Where(static product => !string.IsNullOrWhiteSpace(product.ProductName))
            .ToList();

        if (products.Count == 0 && !string.IsNullOrWhiteSpace(settings.ManualFolder))
        {
            products.Add(new ProductKnowledgeSettings
            {
                ProductName = settings.SelectedProductName
                    ?? settings.DefaultProductName
                    ?? "Default",
                BaseFolder = settings.BaseFolder ?? string.Empty,
                CloseFolder = settings.CloseFolder ?? string.Empty,
                ManualFolders = [settings.ManualFolder],
                DocumentUrls = [],
                IsEnabled = true,
            });
        }

        foreach (var product in products)
        {
            AddProduct(ProductKnowledgeViewModel.FromSettings(product));
        }

        ProductKnowledgeStatusText = Products.Count == 0
            ? "製品別ナレッジ設定は未登録です。"
            : $"製品別ナレッジ設定: {Products.Count} 件";
    }

    private void SelectConfiguredProduct(string? productName)
    {
        if (Products.Count == 0)
        {
            SelectedProductKnowledge = null;
            return;
        }

        SelectedProductKnowledge = Products.FirstOrDefault(product =>
                string.Equals(product.ProductName, productName, StringComparison.OrdinalIgnoreCase))
            ?? Products.FirstOrDefault();
    }

    private void EnsureLaunchContextProductSelected(AiAssistantLaunchContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProductName))
        {
            return;
        }

        var productNameFromContext = context.ProductName.Trim();
        var product = Products.FirstOrDefault(item =>
                context.ProductId.HasValue
                && context.ProductId.Value != Guid.Empty
                && item.ProductId == context.ProductId.Value)
            ?? Products.FirstOrDefault(item =>
                string.Equals(item.ProductName, productNameFromContext, StringComparison.OrdinalIgnoreCase)
                || item.Aliases.Any(alias => string.Equals(alias, productNameFromContext, StringComparison.OrdinalIgnoreCase)));
        if (product is null)
        {
            product = new ProductKnowledgeViewModel
            {
                ProductId = context.ProductId ?? Guid.NewGuid(),
                ProductName = productNameFromContext,
                BaseFolder = context.BaseFolder ?? string.Empty,
                CloseFolder = context.CloseFolder ?? string.Empty,
                ProductPromptFilePath = context.ProductPromptFilePath ?? string.Empty,
                IsEnabled = true,
            };
            AddProduct(product);
            ProductKnowledgeStatusText = $"外部Context製品を新規作成し、検索対象にしました: {productNameFromContext}";
        }
        else
        {
            if (product.ProductId == Guid.Empty && context.ProductId.HasValue)
            {
                product.ProductId = context.ProductId.Value;
            }

            if (string.IsNullOrWhiteSpace(product.ProductPromptFilePath)
                && !string.IsNullOrWhiteSpace(context.ProductPromptFilePath))
            {
                product.ProductPromptFilePath = context.ProductPromptFilePath;
            }

            ProductKnowledgeStatusText = $"外部Context製品を検索対象にしました: {productNameFromContext}";
        }

        SelectedProductKnowledge = product;
    }

    private void AddProduct(ProductKnowledgeViewModel product)
    {
        product.PropertyChanged += (_, _) =>
        {
            RefreshProductContextComputedProperties();
            if (settingsLoaded && !isApplyingSettings)
            {
                ScheduleAutoSave();
            }
        };
        Products.Add(product);
    }

    private void RefreshProductContextComputedProperties()
    {
        OnPropertyChanged(nameof(CurrentProductContextText));
        OnPropertyChanged(nameof(ManualFolderUsageText));
        OnPropertyChanged(nameof(SelectedProductIndexFolder));
    }

    private string BuildCurrentProductContextText()
    {
        var selectedProduct = SelectedProductKnowledge;
        var searchProductName = selectedProduct?.ProductName ?? ProductName;
        var indexFolder = selectedProduct is null
            ? EffectiveAiIndexFolder()
            : productScopedIndexService.GetProductIndexFolder(EffectiveAiIndexFolder(), selectedProduct.ProductName);
        var caseIndexPath = Path.Combine(indexFolder, AiCaseIndexBuilder.IndexFileName);
        var manualIndexPath = Path.Combine(indexFolder, AiManualIndexBuilder.IndexFileName);
        var officialIndexPath = Path.Combine(indexFolder, AiOfficialDocumentIndexBuilder.IndexFileName);

        var builder = new StringBuilder();
        builder.AppendLine($"現在の検索モード: {(selectedProduct is null ? "旧単一マニュアルフォルダ" : "製品別ナレッジ設定")}");
        builder.AppendLine($"現在の外部Context製品: {ValueOrUnset(externalContextProductName)}");
        builder.AppendLine($"現在の検索対象製品: {ValueOrUnset(searchProductName)}");
        builder.AppendLine($"製品別インデックス: {indexFolder}");
        builder.AppendLine($"マニュアルフォルダ数: {selectedProduct?.ManualFolders.Count ?? 0}");
        builder.AppendLine($"公式URL数: {selectedProduct?.DocumentUrls.Count ?? 0}");
        builder.AppendLine($"過去案件インデックス: {(File.Exists(caseIndexPath) ? "作成済み" : "未作成")}");
        builder.AppendLine($"マニュアルインデックス: {(File.Exists(manualIndexPath) ? "作成済み" : "未作成")}");
        builder.AppendLine($"公式URLインデックス: {(File.Exists(officialIndexPath) ? "作成済み" : "未作成")}");

        if (!string.IsNullOrWhiteSpace(externalContextProductName)
            && !string.IsNullOrWhiteSpace(searchProductName)
            && !string.Equals(externalContextProductName, searchProductName, StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"警告: 外部Context製品と検索対象製品が一致していません。外部Context={externalContextProductName}, 検索対象={searchProductName}");
        }

        return builder.ToString();
    }

    private string BuildManualFolderUsageText()
    {
        if (SelectedProductKnowledge is not null)
        {
            return $"現在は製品別設定の ManualFolders を検索・インデックス作成に使用します。ManualFolders={SelectedProductKnowledge.ManualFolders.Count}件、DocumentUrls={SelectedProductKnowledge.DocumentUrls.Count}件。単一「マニュアルフォルダ」欄は製品未選択時のみ使用されます。";
        }

        return string.IsNullOrWhiteSpace(ManualFolder)
            ? "製品別設定が未選択のため、単一「マニュアルフォルダ」欄も未設定です。"
            : $"製品別設定が未選択のため、単一「マニュアルフォルダ」を使用します: {ManualFolder}";
    }

    private static string ValueOrUnset(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(未設定)" : value.Trim();
    }

    private static int GetSourcePriority(
        string? sourceType,
        IReadOnlyList<string> questionTypes,
        bool freshnessSensitive)
    {
        if (freshnessSensitive || questionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return sourceType switch
            {
                "OfficialDoc" => 0,
                "Manual" => 1,
                "ExactPastAnswer" => 3,
                "PastAnswer" => 4,
                "PastCaseNote" => 5,
                _ => 6,
            };
        }

        if (questionTypes.Contains(QuestionTypes.TroubleshootingQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return sourceType switch
            {
                "ExactPastAnswer" => 0,
                "Manual" => 1,
                "OfficialDoc" => 2,
                "PastAnswer" => 3,
                "PastCaseNote" => 4,
                _ => 5,
            };
        }

        return sourceType switch
        {
            "Manual" => 0,
            "OfficialDoc" => 1,
            "ExactPastAnswer" => 2,
            "PastAnswer" => 3,
            "PastCaseNote" => 4,
            _ => 5,
        };
    }

    private IReadOnlyList<ProductKnowledgeSettings> BuildProductKnowledgeSettings()
    {
        return Products
            .Select(static product => product.ToSettings())
            .Where(static product => !string.IsNullOrWhiteSpace(product.ProductName))
            .ToList();
    }

    private void ApplySelectedProductToCurrentFields()
    {
        if (SelectedProductKnowledge is null)
        {
            return;
        }

        ProductName = SelectedProductKnowledge.ProductName;
        BaseFolder = SelectedProductKnowledge.BaseFolder;
        CloseFolder = SelectedProductKnowledge.CloseFolder;
        ManualFolder = SelectedProductKnowledge.ManualFolders.FirstOrDefault() ?? ManualFolder;
        OnPropertyChanged(nameof(SelectedProductIndexFolder));
    }

    private ProductKnowledgeSettings? GetSelectedProductSettings()
    {
        SynchronizeSelectedProductFromCurrentFields();
        return SelectedProductKnowledge?.ToSettings();
    }

    private ProductKnowledgeSettings? ResolveProductForSearch()
    {
        var enabledProducts = Products.Where(static product => product.IsEnabled).ToList();
        ProductKnowledgeViewModel? resolved = null;
        if (!string.IsNullOrWhiteSpace(externalContextProductName))
        {
            resolved = enabledProducts.FirstOrDefault(product =>
                string.Equals(product.ProductName, externalContextProductName, StringComparison.OrdinalIgnoreCase));
        }

        resolved ??= SelectedProductKnowledge is { IsEnabled: true } ? SelectedProductKnowledge : null;
        if (resolved is null && !string.IsNullOrWhiteSpace(ProductName) && ProductName != "製品A")
        {
            resolved = enabledProducts.FirstOrDefault(product =>
                string.Equals(product.ProductName, ProductName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        resolved ??= enabledProducts.FirstOrDefault(product => InquiryMentionsProduct(InquiryText, product));
        return resolved?.ToSettings();
    }

    private static bool InquiryMentionsProduct(string inquiry, ProductKnowledgeViewModel product)
    {
        if (inquiry.Contains(product.ProductName, StringComparison.OrdinalIgnoreCase)
            || product.Aliases.Any(alias => inquiry.Contains(alias, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return string.Equals(product.ProductName, "Checkmarx", StringComparison.OrdinalIgnoreCase)
            ? inquiry.Contains("CxSAST", StringComparison.OrdinalIgnoreCase) || inquiry.Contains("SAST", StringComparison.OrdinalIgnoreCase)
            : string.Equals(product.ProductName, "HelixQAC", StringComparison.OrdinalIgnoreCase)
                && (inquiry.Contains("Helix QAC", StringComparison.OrdinalIgnoreCase) || inquiry.Contains("QAC", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshPastAnswerCandidateCoreAsync(CancellationToken cancellationToken = default)
    {
        var searchLimit = Math.Max(12, Math.Max(1, MaxEvidenceItems) * 2);
        var resolvedProduct = ResolveProductForSearch();
        allowPastAnswerAutoSelection = resolvedProduct is not null;
        lastInquiryFocus = inquiryFocusExtractor.Extract(InquiryText, BuildCurrentCaseContext());
        InquiryFocusSummaryText = FormatInquiryFocusSummary(lastInquiryFocus);

        IReadOnlyList<SearchSource> inquiryMatches;
        IReadOnlyList<SearchSource> supportNumberMatches = [];
        if (resolvedProduct is null)
        {
            inquiryMatches = string.IsNullOrWhiteSpace(InquiryText)
                ? []
                : await productScopedSearchService.SearchPastAnswersAcrossProductsAsync(
                    BuildProductKnowledgeSettings(),
                    EffectiveAiIndexFolder(),
                    InquiryText,
                    searchLimit,
                    cancellationToken);
        }
        else
        {
            inquiryMatches = string.IsNullOrWhiteSpace(InquiryText)
                ? []
                : await productScopedSearchService.SearchPastAnswersAsync(
                    resolvedProduct,
                    EffectiveAiIndexFolder(),
                    InquiryText,
                    searchLimit,
                    cancellationToken);

            if (!inquiryManuallyEdited && !string.IsNullOrWhiteSpace(SupportNumber))
            {
                supportNumberMatches = await productScopedSearchService.SearchPastAnswersBySupportNumberAsync(
                    resolvedProduct,
                    EffectiveAiIndexFolder(),
                    SupportNumber,
                    searchLimit,
                    cancellationToken);
            }
        }

        lastSearchSources = MergeSearchSources(supportNumberMatches, inquiryMatches, searchLimit);
        lastManualSearchSources = [];
        lastOfficialDocumentSearchSources = [];

        var combined = BuildCombinedSearchSources();
        ReplaceSearchResults(combined);
        UpdatePastAnswerCandidate(resolvedProduct);
        var appliedSupportNumberAnswer = TryApplySupportNumberPastAnswer();
        SearchResultsText = FormatSearchResults(combined);
        UpdatePromptSummary();
        var summary = SearchSourceSummaryBuilder.Build(
            SearchResults,
            SourceTypeFilter,
            MaxEvidenceItems,
            HighScoreThreshold,
            MinimumDisplayScore,
            lastInquiryFocus.IsFreshnessSensitive,
            EnableTopNFallback);
        UpdateOfficialDocDiagnostics(summary);
        if (!appliedSupportNumberAnswer)
        {
            GenerationState = "Ready";
        }
        UpdateRagDiagnostics();
    }

    private static IReadOnlyList<SearchSource> MergeSearchSources(
        IEnumerable<SearchSource> preferred,
        IEnumerable<SearchSource> additional,
        int maxResults)
    {
        return preferred
            .Concat(additional)
            .GroupBy(static source => source.SourceId, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(source => string.Equals(
                    source.MatchKind,
                    PastAnswerMatchKinds.SupportNumber,
                    StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(static source => source.Score ?? 0)
                .First())
            .OrderByDescending(source => string.Equals(
                source.MatchKind,
                PastAnswerMatchKinds.SupportNumber,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static source => source.Score ?? 0)
            .Take(Math.Max(1, maxResults))
            .ToList();
    }

    private void UpdatePastAnswerCandidate(ProductKnowledgeSettings? resolvedProduct)
    {
        selectedPastAnswerCandidate = SearchResults
            .Select(static item => item.Source)
            .Where(static source => string.Equals(source.SourceType, "ExactPastAnswer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static source => source.Score ?? 0)
            .FirstOrDefault();
        if (selectedPastAnswerCandidate is null)
        {
            PastAnswerCandidateText = "過去回答候補なし";
            return;
        }

        var sameProduct = resolvedProduct is not null &&
            string.Equals(resolvedProduct.ProductName, selectedPastAnswerCandidate.ProductName, StringComparison.OrdinalIgnoreCase);
        allowPastAnswerAutoSelection = sameProduct;
        var productWarning = sameProduct
            ? string.Empty
            : $"{Environment.NewLine}製品が異なる、または未解決のため自動採用しません。";
        PastAnswerCandidateText = string.Join(Environment.NewLine,
        [
            "過去回答候補あり",
            $"一致度: {selectedPastAnswerCandidate.Score ?? 0:0.00}",
            $"一致種別: {ValueOrUnset(selectedPastAnswerCandidate.MatchKind)}",
            $"製品: {ValueOrUnset(selectedPastAnswerCandidate.ProductName)}",
            $"サポート番号: {ValueOrUnset(selectedPastAnswerCandidate.SupportNumber)}",
            $"更新日: {selectedPastAnswerCandidate.RetrievedAt?.ToString("yyyy/MM/dd HH:mm") ?? "(未設定)"}",
            $"回答本文: {selectedPastAnswerCandidate.Text}{productWarning}",
        ]);
    }

    private bool TryApplySupportNumberPastAnswer()
    {
        if (selectedPastAnswerCandidate is null
            || !allowPastAnswerAutoSelection
            || !string.Equals(
                selectedPastAnswerCandidate.MatchKind,
                PastAnswerMatchKinds.SupportNumber,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ApplyPastAnswerCandidate("案件番号に一致する保存済み回答を表示しました。");
        return true;
    }

    private void UsePastAnswerWithoutLlm()
    {
        ApplyPastAnswerCandidate("過去回答をLLMなしで使用しました。");
    }

    private void ApplyPastAnswerToDraft()
    {
        ApplyPastAnswerCandidate("過去回答を回答案へ反映しました。");
    }

    private void ApplyPastAnswerCandidate(string message)
    {
        if (selectedPastAnswerCandidate is null)
        {
            StatusMessage = "使用できる過去回答候補がありません。";
            return;
        }

        if (!allowPastAnswerAutoSelection)
        {
            GenerationState = "NeedsConfiguration";
            StatusMessage = "別製品または製品未解決の過去回答は自動採用できません。製品を確認してください。";
            return;
        }

        CustomerReplyDraft = selectedPastAnswerCandidate.Text;
        InternalMemo = selectedPastAnswerCandidate.InternalMemo ?? string.Empty;
        EvidenceText = $"- [ExactPastAnswer] {selectedPastAnswerCandidate.Title}";
        ConfidenceText = $"{selectedPastAnswerCandidate.Score ?? 0:0.00}";
        AnswerReadinessText = "ReadyFromExactPastAnswer";
        GenerationState = "Completed";
        GenerationSkippedReason = "ExactPastAnswerUsedWithoutLlm";
        StatusMessage = message;
        LastOperationResult = $"Past answer applied. SourceId={selectedPastAnswerCandidate.SourceId}; LLM=false";
        UpdateRagDiagnostics();
    }

    private async Task PolishPastAnswerAsync()
    {
        if (selectedPastAnswerCandidate is null || !allowPastAnswerAutoSelection)
        {
            ApplyPastAnswerCandidate("過去回答候補を確認してください。");
            return;
        }

        var candidate = SearchResults.FirstOrDefault(item => item.SourceId == selectedPastAnswerCandidate.SourceId);
        candidate?.SetSelectedProgrammatically(true);
        pastAnswerPolishRequested = true;
        try
        {
            await GenerateDraftAsync();
        }
        finally
        {
            pastAnswerPolishRequested = false;
        }
    }

    private void SkipGeneration(string state, string reason, string message)
    {
        GenerationState = state;
        GenerationSkippedReason = reason;
        StatusMessage = message;
        LastOperationResult = $"Generation skipped. Reason={reason}; Model={ValueOrUnset(ChatModel)}";
        GenerationDiagnosticsText = $"Generation skipped reason: {reason}{Environment.NewLine}{message}";
        UpdateRagDiagnostics();
    }

    private string BuildUnresolvedModelMessage()
    {
        var models = AvailableModels.Count == 0
            ? "- (なし)"
            : string.Join(Environment.NewLine, AvailableModels.Select(static model => $"- {model}"));
        return $"回答モデルを解決できませんでした。{Environment.NewLine}利用可能モデル:{Environment.NewLine}{models}";
    }

    private bool HasKnowledgeForProduct(ProductKnowledgeSettings product)
    {
        var folder = productScopedIndexService.GetProductIndexFolder(EffectiveAiIndexFolder(), product.ProductName);
        return File.Exists(Path.Combine(folder, KnowledgeManifest.FileName))
            || File.Exists(Path.Combine(folder, AiCaseIndexBuilder.IndexFileName))
            || File.Exists(Path.Combine(folder, CaseAnswerPairIndexDocument.FileName))
            || File.Exists(Path.Combine(folder, AiManualIndexBuilder.IndexFileName))
            || File.Exists(Path.Combine(folder, AiOfficialDocumentIndexBuilder.IndexFileName))
            || File.Exists(Path.Combine(folder, "curated-facts.json"));
    }

    private void UpdateRagDiagnostics()
    {
        var resolvedProduct = ResolveProductForSearch();
        var questionTypes = new QuestionClassifier()
            .Classify(InquiryText, lastInquiryFocus)
            .QuestionTypes;
        var indexPath = resolvedProduct is null
            ? EffectiveAiIndexFolder()
            : productScopedIndexService.GetProductIndexFolder(EffectiveAiIndexFolder(), resolvedProduct.ProductName);
        var manifestPath = Path.Combine(indexPath, KnowledgeManifest.FileName);
        var lastUpdated = File.Exists(manifestPath)
            ? File.GetLastWriteTime(manifestPath).ToString("yyyy-MM-dd HH:mm:ss")
            : "(未設定)";
        var selectedEvidence = SearchResults.Count(static source => source.IsSelected);
        var exactMatches = SearchResults.Count(static source =>
            string.Equals(source.SourceType, "ExactPastAnswer", StringComparison.OrdinalIgnoreCase));
        var nearMatches = SearchResults.Count(static source =>
            string.Equals(source.Source.MatchKind, PastAnswerMatchKinds.NearDuplicate, StringComparison.OrdinalIgnoreCase));
        RagDiagnosticsText = string.Join(Environment.NewLine,
        [
            $"Resolved product: {ValueOrUnset(resolvedProduct?.ProductName)}",
            $"Selected model: {ValueOrUnset(ChatModel)}",
            $"Quality mode: {AnswerQualityMode}",
            $"Model source: {ModelResolutionSource}",
            $"Question type: {string.Join(", ", questionTypes)}",
            $"Exact past answer matches: {exactMatches}",
            $"Near duplicate matches: {nearMatches}",
            $"Manual results: {SearchResults.Count(static source => source.SourceType == "Manual")}",
            $"OfficialDoc results: {SearchResults.Count(static source => source.SourceType == "OfficialDoc")}",
            $"PastCase results: {SearchResults.Count(static source => source.SourceType is "PastCaseNote" or "PastAnswer")}",
            $"Selected evidence: {selectedEvidence}",
            $"Index path: {indexPath}",
            $"Index last updated: {lastUpdated}",
            $"Generation state: {GenerationState}",
            $"Generation skipped reason: {ValueOrUnset(GenerationSkippedReason)}",
        ]);
    }

    private void SynchronizeSelectedProductFromCurrentFields()
    {
        if (SelectedProductKnowledge is null ||
            string.IsNullOrWhiteSpace(ProductName) ||
            !string.Equals(SelectedProductKnowledge.ProductName, ProductName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedProductKnowledge.ProductName = ProductName.Trim();
        if (!string.IsNullOrWhiteSpace(BaseFolder))
        {
            SelectedProductKnowledge.BaseFolder = BaseFolder.Trim();
        }

        if (!string.IsNullOrWhiteSpace(CloseFolder))
        {
            SelectedProductKnowledge.CloseFolder = CloseFolder.Trim();
        }
    }

    private static string FirstNonWhiteSpace(params string[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FormatLaunchContextDiagnostic(
        AiAssistantLaunchContext context,
        bool caseFolderExists,
        bool noteFileExists)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Launch context loaded.");
        builder.AppendLine($"Source: {context.Source}");
        builder.AppendLine($"ProductName: {context.ProductName}");
        builder.AppendLine($"CaseFolderPath exists: {caseFolderExists}");
        builder.AppendLine($"NoteFilePath exists: {noteFileExists}");
        builder.AppendLine($"Has selected text: {!string.IsNullOrWhiteSpace(context.SelectedText)}");
        builder.AppendLine($"Has current note text: {!string.IsNullOrWhiteSpace(context.CurrentNoteText)}");
        builder.AppendLine($"Has inquiry text: {!string.IsNullOrWhiteSpace(context.InquiryText)}");
        return builder.ToString();
    }

    private static string SanitizeLogToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var token = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return token.Length <= 80 ? token : token[..80] + "...";
    }

    private void ApplyAppearance()
    {
        appearanceService.Apply(UiLanguage, UseDarkMode);
    }

    private void ReplaceNotes(IEnumerable<NoteSnapshot> notes)
    {
        Notes.Clear();
        foreach (var note in notes)
        {
            Notes.Add(note);
        }

        SelectedNote = Notes.FirstOrDefault();
        UpdatePromptSummary();
    }

    private void ReplaceSearchResults(IReadOnlyList<SearchSource> sources)
    {
        SearchResults.Clear();
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var shouldSelect = FreshnessEvidenceAutoSelector.ShouldAutoSelect(
                source,
                lastInquiryFocus?.IsFreshnessSensitive == true,
                HighScoreThreshold);
            if (!allowPastAnswerAutoSelection &&
                source.SourceType is "ExactPastAnswer" or "PastAnswer")
            {
                shouldSelect = false;
            }

            var viewModel = new SearchSourceViewModel(source, shouldSelect);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SearchSourceViewModel.IsSelected))
                {
                    UpdatePromptSummary();
                }
            };
            SearchResults.Add(viewModel);
        }

        RefreshFilteredSearchResults(updateSummary: false);
        SelectedSearchResult = FilteredSearchResults.FirstOrDefault();
        MarkUsedSources(lastUsedSources);
        UpdatePromptSummary();
    }

    private void RefreshFilteredSearchResults(bool updateSummary = true)
    {
        var currentSelectedId = SelectedSearchResult?.SourceId;
        FilteredSearchResults.Clear();
        foreach (var item in SearchSourceFiltering.Apply(SearchResults, SourceTypeFilter, MinimumDisplayScore))
        {
            FilteredSearchResults.Add(item);
        }

        FilteredSearchResultCount = FilteredSearchResults.Count;
        SelectedSearchResult = FilteredSearchResults.FirstOrDefault(item => item.SourceId == currentSelectedId)
            ?? FilteredSearchResults.FirstOrDefault();

        if (updateSummary)
        {
            UpdatePromptSummary();
        }
    }

    private IReadOnlyList<SearchSource> BuildCombinedSearchSources()
    {
        var questionTypes = new QuestionClassifier()
            .Classify(InquiryText, lastInquiryFocus)
            .QuestionTypes;
        return lastOfficialDocumentSearchSources
            .Concat(lastManualSearchSources)
            .Concat(lastSearchSources)
            .OrderBy(source => GetSourcePriority(
                source.SourceType,
                questionTypes,
                lastInquiryFocus?.IsFreshnessSensitive == true))
            .ThenByDescending(static source => source.Score ?? 0)
            .ThenBy(static source => source.SourceType, StringComparer.Ordinal)
            .ThenBy(static source => source.SourceId, StringComparer.Ordinal)
            .ToList();
    }

    private AnswerDraftRequest BuildDraftRequest()
    {
        var sources = BuildSearchSources();
        var effectiveProductName = ResolveEffectiveProductName(sources);
        var caseContext = BuildCurrentCaseContext(effectiveProductName);
        if (inquiryManuallyEdited)
        {
            caseContext = caseContext with { Notes = [] };
        }

        var inquiryFocus = lastInquiryFocus ?? inquiryFocusExtractor.Extract(InquiryText, caseContext);
        var settings = ApplyAutomaticGenerationRoute(BuildSettings(), sources, inquiryFocus);
        var factResolution = new FactResolver().Resolve(
            effectiveProductName,
            settings.AiIndexFolder,
            InquiryText,
            inquiryFocus);
        var selectedSources = EvidenceSourceSelector.Select(
            sources,
            caseContext,
            factResolution,
            settings.MaxEvidenceItems,
            settings.MaxPromptChars);
        var selectedProduct = Products.FirstOrDefault(product =>
            string.Equals(product.ProductName, effectiveProductName, StringComparison.OrdinalIgnoreCase));
        var promptInstructions = SupportPromptFileLoader.Load(
            selectedProduct?.ProductPromptFilePath,
            SupportToolSettingsFilePath);
        return new AnswerDraftRequest
        {
            Case = caseContext,
            InquiryText = InquiryText,
            CommonInstruction = promptInstructions.CommonInstruction,
            ProductInstruction = promptInstructions.ProductInstruction,
            AttachmentFileNames = CollectAttachmentFileNames(caseContext.CaseFolderPath),
            InstructionWarnings = promptInstructions.Warnings,
            InquiryFocus = inquiryFocus,
            UserInstruction = pastAnswerPolishRequested
                ? $"以下は同一またはほぼ同一の問い合わせに対して過去に実際に使用した回答です。技術的内容を変更せず、今回のお客様向けに必要な範囲だけ整えてください。{Environment.NewLine}{AdditionalInstruction}"
                : AdditionalInstruction,
            Sources = selectedSources,
            FactResolution = factResolution,
            Settings = settings,
            RequestedAt = DateTimeOffset.Now,
        };
    }

    private static IReadOnlyList<string> CollectAttachmentFileNames(string? caseFolderPath)
    {
        if (string.IsNullOrWhiteSpace(caseFolderPath) || !Directory.Exists(caseFolderPath))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(caseFolderPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(caseFolderPath, path))
                .OrderBy(static path => path, StringComparer.CurrentCultureIgnoreCase)
                .Take(200)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private AiAssistantSettings ApplyAutomaticGenerationRoute(
        AiAssistantSettings settings,
        IReadOnlyList<SearchSource> sources,
        InquiryFocus inquiryFocus)
    {
        if (!ShouldUseFastManualRoute(settings, sources, inquiryFocus))
        {
            return settings;
        }

        var availableModel = ResolveFastManualModel()
            ?? throw new InvalidOperationException("Fast manual model could not be resolved.");
        var profile = ModelCapabilityProfiles.Resolve(availableModel, modelCapabilityProfiles);
        return settings with
        {
            MaxEvidenceItems = Math.Min(FastManualMaxEvidenceItems, Math.Max(1, settings.MaxEvidenceItems)),
            MaxPromptChars = Math.Min(FastManualMaxPromptChars, Math.Max(1200, settings.MaxPromptChars)),
            DisableThinking = true,
            LlmProvider = settings.LlmProvider with
            {
                ChatModel = availableModel,
                Temperature = profile.Temperature,
                MaxOutputTokens = Math.Min(
                    FastManualMaxOutputTokens,
                    Math.Max(240, Math.Min(settings.LlmProvider.MaxOutputTokens, profile.MaxOutputTokens))),
                ContextWindowTokens = Math.Min(4096, Math.Max(2048, settings.LlmProvider.ContextWindowTokens)),
                TimeoutSeconds = FastManualTimeoutSeconds,
                ThinkingParameterType = profile.ThinkingParameterType,
                ThinkingValue = profile.ThinkingValue,
                StructuredOutputMode = profile.StructuredOutputMode,
            },
        };
    }

    private bool ShouldUseFastManualRoute(
        AiAssistantSettings settings,
        IReadOnlyList<SearchSource> sources,
        InquiryFocus inquiryFocus)
    {
        return string.Equals(NormalizeProvider(settings.LlmProvider.Provider), "Ollama", StringComparison.Ordinal)
            && !pastAnswerPolishRequested
            && InquiryText.Trim().Length <= 500
            && inquiryFocus.IsFreshnessSensitive == false
            && sources.Count > 0
            && sources.All(static source =>
                string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
            && ResolveFastManualModel() is not null;
    }

    private string? ResolveFastManualModel()
    {
        foreach (var candidate in new[] { FastManualPreferredModelName, FastManualFallbackModelName })
        {
            var available = AvailableModels.FirstOrDefault(model => ModelNamesEquivalent(model, candidate));
            if (!string.IsNullOrWhiteSpace(available))
            {
                return available;
            }
        }

        return null;
    }

    private static bool ModelNamesEquivalent(string left, string right)
    {
        return string.Equals(RemoveLatestModelTag(left), RemoveLatestModelTag(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveLatestModelTag(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^7]
            : trimmed;
    }

    private CaseContext BuildCurrentCaseContext(string? productNameOverride = null)
    {
        return new CaseContext
        {
            Source = "SupportCaseManager.AiAssistant.App",
            ProductName = string.IsNullOrWhiteSpace(productNameOverride) ? ProductName : productNameOverride,
            BaseFolder = BaseFolder,
            CloseFolder = CloseFolder,
            CaseFolderPath = CaseFolderPath,
            CompanyName = CompanyName,
            CustomerName = CustomerName,
            SupportNumber = SupportNumber,
            Status = Status,
            ReceptionDate = DateOnly.TryParse(ReceptionDate, out var parsedDate) ? parsedDate : null,
            Notes = Notes.ToList(),
        };
    }

    private string ResolveEffectiveProductName(IReadOnlyList<SearchSource>? sources = null)
    {
        if (!string.IsNullOrWhiteSpace(externalContextProductName))
        {
            return externalContextProductName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(SelectedProductKnowledge?.ProductName))
        {
            return SelectedProductKnowledge.ProductName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ProductName) &&
            !string.Equals(ProductName.Trim(), "製品A", StringComparison.OrdinalIgnoreCase))
        {
            return ProductName.Trim();
        }

        if (InquiryText.Contains("Checkmarx", StringComparison.OrdinalIgnoreCase) ||
            InquiryText.Contains("CxSAST", StringComparison.OrdinalIgnoreCase) ||
            InquiryText.Contains("ＣｘＳＡＳＴ", StringComparison.OrdinalIgnoreCase))
        {
            return "Checkmarx";
        }

        var sourceProductName = sources?
            .Select(static source => source.ProductName)
            .FirstOrDefault(static productName => !string.IsNullOrWhiteSpace(productName));
        if (!string.IsNullOrWhiteSpace(sourceProductName))
        {
            return sourceProductName.Trim();
        }

        return ProductName;
    }

    private IReadOnlyList<SearchSource> BuildSearchSources()
    {
        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            SearchResults,
            SourceTypeFilter,
            MaxEvidenceItems,
            HighScoreThreshold,
            MinimumDisplayScore,
            lastInquiryFocus?.IsFreshnessSensitive == true,
            EnableTopNFallback);
        ApplySelectionSummary(summary);
        return summary.Selection.Sources;
    }

    private void ApplyDraftResult(AnswerDraftResult result)
    {
        CustomerReplyDraft = result.CustomerReplyDraft;
        InternalMemo = result.InternalMemo;
        NeedConfirmationsText = result.NeedConfirmations.Count == 0
            ? "(なし)"
            : string.Join(Environment.NewLine, result.NeedConfirmations.Select(item => $"- [{item.Priority}] {item.Question} / {item.Reason}"));
        var factResolution = lastRequest?.FactResolution;
        AnswerReadinessText = factResolution?.AnswerReadiness ?? AnswerReadiness.InsufficientEvidence;
        ResolvedFactsText = factResolution?.ResolvedFacts.Count > 0
            ? string.Join(Environment.NewLine, factResolution.ResolvedFacts.Select(fact =>
                $"- {fact.Key}={fact.Value} [{fact.Status}/{fact.Confidence}] ({fact.SourceType})"))
            : "(なし)";
        EvidenceText = result.Evidence.Count == 0
            ? "(なし)"
            : string.Join(Environment.NewLine, result.Evidence.Select(item => $"- [{item.SourceType}] {item.Title}"));
        ConfidenceText = $"{result.Confidence:0.00}";
        WarningsText = result.Warnings.Count == 0
            ? "(なし)"
            : string.Join(Environment.NewLine, result.Warnings.Select(warning => $"- {warning}"));
    }

    private void UpdatePromptSummary()
    {
        if (isUpdatingPromptSummary)
        {
            return;
        }

        try
        {
            isUpdatingPromptSummary = true;
            lastInquiryFocus = inquiryFocusExtractor.Extract(InquiryText, BuildCurrentCaseContext());
            InquiryFocusSummaryText = FormatInquiryFocusSummary(lastInquiryFocus);
            RefreshFilteredSearchResults(updateSummary: false);

            var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
                SearchResults,
                SourceTypeFilter,
                MaxEvidenceItems,
                HighScoreThreshold,
                MinimumDisplayScore,
                lastInquiryFocus?.IsFreshnessSensitive == true,
                EnableTopNFallback);
            ApplySelectionSummary(summary);
            UpdateOfficialDocDiagnostics(summary);
            EvidenceCount = summary.Selection.Sources.Count;
            PromptApproxChars = SafeLength(InquiryText)
                + SafeLength(AdditionalInstruction)
                + summary.Selection.Sources.Sum(static source => SafeLength(source.Text))
                + (inquiryManuallyEdited ? 0 : Notes.Sum(static note => SafeLength(note.Text)));
        }
        finally
        {
            isUpdatingPromptSummary = false;
        }
    }

    private void ApplySelectionSummary(SearchSourceSummary summary)
    {
        var selection = summary.Selection;
        SearchResultCount = selection.SearchResultCount;
        FilteredSearchResultCount = summary.FilteredCount;
        SelectedEvidenceCount = selection.SelectedCount;
        PastCaseNoteSelectedCount = selection.PastCaseNoteSelectedCount;
        ManualSelectedCount = selection.ManualSelectedCount;
        OfficialDocSelectedCount = selection.OfficialDocSelectedCount;
        PastCaseNoteSendCount = selection.PastCaseNoteSendCount;
        ManualSendCount = selection.ManualSendCount;
        OfficialDocSendCount = selection.OfficialDocSendCount;
        EvidenceToSendCount = selection.Sources.Count;
        ExcludedByLimitCount = selection.ExcludedSelectedCount;
        EvidenceLimitWarningText = selection.Warning;
        EvidenceSummaryText = FormatEvidenceSummary(summary);
    }

    private static string FormatOllamaConnectionResult(OllamaConnectionCheckResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.IsSuccess ? "接続成功" : "接続失敗");
        builder.AppendLine($"Endpoint: {result.Endpoint}");
        builder.AppendLine($"選択モデル: {result.SelectedModel ?? "(未設定)"}");
        builder.AppendLine($"利用可能モデル数: {result.AvailableModels.Count}");
        builder.AppendLine($"選択モデル存在: {(result.SelectedModelExists ? "はい" : "いいえ")}");
        builder.AppendLine($"メッセージ: {result.Message}");

        if (!result.IsSuccess)
        {
            builder.AppendLine("対処ヒント:");
            builder.AppendLine("- Ollamaが起動しているか確認してください。");
            builder.AppendLine("- Endpointが正しいか確認してください。");
            builder.AppendLine("- モデルをpull済みか確認してください。");
        }

        if (result.AvailableModels.Count > 0)
        {
            builder.AppendLine("モデル一覧:");
            foreach (var model in result.AvailableModels)
            {
                builder.AppendLine($"- {model}");
            }
        }

        if (result.ChatTestAttempted)
        {
            builder.AppendLine($"Chat test: {(result.ChatTestSuccess ? "success" : "failure")}");
            builder.AppendLine($"Content returned: {(result.ChatContentReturned ? "yes" : "no")}");
            builder.AppendLine($"Thinking returned: {(result.ChatThinkingReturned ? "yes" : "no")}");
            builder.AppendLine($"Done reason: {ValueOrUnset(result.ChatDoneReason)}");
            if (result.ChatTotalDuration is not null)
            {
                builder.AppendLine($"Duration: {result.ChatTotalDuration} ns");
            }

            if (!string.IsNullOrWhiteSpace(result.ChatTestMessage))
            {
                builder.AppendLine(result.ChatTestMessage);
            }

            foreach (var warning in result.ChatTestWarnings)
            {
                builder.AppendLine($"警告: {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(ModelRecommendationHelper.BuildRecommendationText(result.SelectedModel));

        return builder.ToString();
    }

    private static string FormatOllamaProductionMiniTestResult(
        bool isSuccess,
        LlmProviderSettings settings,
        PromptMessages promptMessages,
        LlmGenerationResult? generation,
        TimeSpan elapsed,
        Exception? error)
    {
        var diagnostics = promptMessages.Diagnostics;
        var builder = new StringBuilder();
        builder.AppendLine("Ollama本番生成ミニテスト");
        builder.AppendLine($"result: {(isSuccess ? "success" : "failed")}");
        builder.AppendLine("test mode: direct chat micro prompt");
        builder.AppendLine($"model: {settings.ChatModel}");
        builder.AppendLine($"configured timeout seconds: {settings.TimeoutSeconds}");
        builder.AppendLine($"elapsed seconds: {elapsed.TotalSeconds:0.0}");
        builder.AppendLine($"max output tokens: {settings.MaxOutputTokens}");
        builder.AppendLine($"context window tokens: {settings.ContextWindowTokens}");
        builder.AppendLine($"Configured max prompt chars: {diagnostics.ConfiguredMaxPromptChars}");
        builder.AppendLine($"Final prompt chars: {diagnostics.FinalPromptChars}");
        builder.AppendLine($"System chars: {diagnostics.SystemChars}");
        builder.AppendLine($"Inquiry chars: {diagnostics.InquiryChars}");
        builder.AppendLine($"Evidence chars: {diagnostics.EvidenceChars}");
        builder.AppendLine($"evidence count: {diagnostics.EvidenceCount}");
        builder.AppendLine("think:false sent: yes");
        builder.AppendLine($"content returned: {(generation?.ContentReturned == true ? "yes" : "no")}");
        builder.AppendLine($"thinking returned: {(generation is null ? "unknown" : generation.ThinkingReturned ? "yes" : "no")}");
        builder.AppendLine($"done_reason: {generation?.DoneReason ?? "(取得不可)"}");
        if (error is not null)
        {
            builder.AppendLine($"error: {FormatExceptionForUi(error)}");
        }

        return builder.ToString();
    }

    private void UpdateModelRecommendationText()
    {
        ModelRecommendationText = ModelRecommendationHelper.BuildRecommendationText(ChatModel);
    }

    private void UpdateOfficialDocDiagnostics(SearchSourceSummary summary)
    {
        var selectedSources = SearchResults
            .Where(static item => item.IsSelected)
            .Select(static item => item.Source)
            .ToList();
        OfficialDocDiagnosticsText = OfficialDocDiagnosticsBuilder.Build(
            GetSelectedProductSettings(),
            EffectiveAiIndexFolder(),
            lastInquiryFocus,
            SearchResults.Select(static item => item.Source).ToList(),
            selectedSources,
            summary.Selection.Sources);
    }

    private static bool ShouldSkipFreshnessWithoutOfficialDoc(AnswerDraftRequest request)
    {
        return request.InquiryFocus?.IsFreshnessSensitive == true &&
            request.Sources.All(static source => !string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanBuildManualTimeoutFallback(AnswerDraftRequest request, Exception exception)
    {
        return IsTimeoutException(exception)
            && request.Sources.Any(static source => string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTimeoutException(Exception exception)
    {
        return exception is TimeoutException or TaskCanceledException
            || exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || (exception.InnerException is not null && IsTimeoutException(exception.InnerException));
    }

    private static AnswerDraftResult BuildManualTimeoutFallbackResult(AnswerDraftRequest request, Exception exception)
    {
        var manuals = request.Sources
            .Where(static source => string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static source => source.Score ?? 0)
            .Take(2)
            .ToList();
        var reply = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.Case.CompanyName))
        {
            reply.AppendLine(request.Case.CompanyName.Trim());
        }

        reply.AppendLine(string.IsNullOrWhiteSpace(request.Case.CustomerName)
            ? "ご担当者様"
            : $"{request.Case.CustomerName.Trim()} 様");
        reply.AppendLine();
        reply.AppendLine("お問い合わせいただきありがとうございます。");
        reply.AppendLine("マニュアルで確認できた関連手順を以下に記載します。");
        reply.AppendLine();
        foreach (var manual in manuals)
        {
            reply.AppendLine($"■ {manual.Title}");
            reply.AppendLine(BuildFocusedManualExcerpt(manual.Text, request.InquiryText, 700));
            reply.AppendLine();
        }

        reply.AppendLine("対象バージョンやご利用のビルド環境によって手順が異なる場合がありますので、該当条件をご確認のうえ実施してください。");
        reply.AppendLine("以上、よろしくお願いいたします。");

        var evidence = manuals.Select(manual => new EvidenceItem
        {
            SourceId = manual.SourceId,
            SourceType = manual.SourceType,
            Title = manual.Title,
            Excerpt = BuildFocusedManualExcerpt(manual.Text, request.InquiryText, 240),
            FilePath = manual.FilePath,
            SupportNumber = manual.SupportNumber,
            Relevance = Math.Clamp(manual.Score ?? 0, 0, 1),
        }).ToList();
        var confidence = evidence.Count == 0
            ? 0
            : Math.Round(Math.Min(0.65, evidence.Average(static item => item.Relevance)), 2);

        return new AnswerDraftResult
        {
            CustomerReplyDraft = reply.ToString().Trim(),
            InternalMemo = $"LLMがタイムアウトしたため、選択済みマニュアル根拠をそのまま回答案へ反映しました。モデル={request.Settings.LlmProvider.ChatModel}; 根拠={manuals.Count}; エラー={exception.GetType().Name}",
            NeedConfirmations =
            [
                new NeedConfirmationItem
                {
                    Question = "対象バージョンとビルド環境に合う手順か確認してください。",
                    Reason = "LLMによる要約が完了せず、マニュアル抜粋を直接提示しているため。",
                    Priority = "Normal",
                    RelatedSourceIds = manuals.Select(static source => source.SourceId).ToList(),
                },
            ],
            Evidence = evidence,
            Confidence = confidence,
            Warnings = ["LLMタイムアウトのため、マニュアル抜粋を使った保守的な回答案です。送信前に内容を確認してください。"],
            GeneratedAt = DateTimeOffset.Now,
        };
    }

    private static string BuildFocusedManualExcerpt(string text, string inquiry, int maxLength)
    {
        var normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        var keywords = Regex.Matches(inquiry ?? string.Empty, @"[A-Za-z][A-Za-z0-9_.-]{1,}")
            .Select(static match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static value => value.Length)
            .ToList();
        var matchIndex = keywords
            .Select(keyword => normalized.IndexOf(keyword, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(static index => index >= 0, -1);
        var start = matchIndex < 0
            ? 0
            : Math.Clamp(matchIndex - (maxLength / 4), 0, Math.Max(0, normalized.Length - maxLength));
        var excerpt = normalized.Substring(start, Math.Min(maxLength, normalized.Length - start));
        return $"{(start > 0 ? "..." : string.Empty)}{excerpt}{(start + excerpt.Length < normalized.Length ? "..." : string.Empty)}";
    }

    private static AnswerDraftResult BuildNoEvidenceSkippedResult()
    {
        return new AnswerDraftResult
        {
            CustomerReplyDraft = """
                選択された根拠が0件のため、現時点では回答案を生成していません。
                検索結果から回答に使用する根拠を選択したうえで、再度生成してください。
                """,
            InternalMemo = "根拠0件時は生成しない設定がONのため、LLM呼び出しをスキップしました。",
            NeedConfirmations =
            [
                new NeedConfirmationItem
                {
                    Question = "回答に使用する根拠を選択してください。",
                    Reason = "根拠0件では安全に回答できないため。",
                    Priority = "High",
                },
            ],
            Evidence = [],
            Confidence = 0,
            Warnings = ["根拠0件のためLLM呼び出しをスキップしました。"],
            GeneratedAt = DateTimeOffset.Now,
        };
    }

    private static AnswerDraftResult BuildFreshnessNoOfficialDocResult(AnswerDraftRequest request)
    {
        var targetVersions = request.InquiryFocus?.TargetVersions.Count > 0
            ? string.Join(", ", request.InquiryFocus.TargetVersions)
            : "(未検出)";

        return new AnswerDraftResult
        {
            CustomerReplyDraft = """
                ご申告内容について、最新バージョンやEP/HFなど鮮度が重要な情報として確認が必要です。
                現時点でAI回答支援に投入できる公式ドキュメント根拠が見つかっていないため、過去案件やローカル資料だけを根拠に最新情報として断定することはできません。
                公式ドキュメントのインデックス作成またはメーカー公式情報の確認後、対象バージョンとリリース情報を再確認して回答します。
                """,
            InternalMemo = $"FreshnessSensitive=true かつ OfficialDoc=0 のため、LLM呼び出しをスキップしました。TargetVersions={targetVersions}; SelectedEvidence={request.Sources.Count}; Manual={request.Sources.Count(static source => string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase))}; PastCaseNote={request.Sources.Count(static source => string.Equals(source.SourceType, "PastCaseNote", StringComparison.OrdinalIgnoreCase))}",
            NeedConfirmations =
            [
                new NeedConfirmationItem
                {
                    Question = "公式ドキュメントのインデックスを作成し、対象バージョンのRelease Notes / Hotfix / Engine Pack情報を確認してください。",
                    Reason = "過去案件やローカル資料だけでは最新情報として断定できません。",
                    Priority = "High",
                },
            ],
            Evidence = [],
            Confidence = 0.2,
            Warnings =
            [
                "FreshnessSensitive=true ですが OfficialDoc 根拠が0件のため、LLM回答生成をスキップしました。",
                "PastCaseNote/Manual の内容を最新情報として断定しないでください。",
            ],
            GeneratedAt = DateTimeOffset.Now,
        };
    }

    private string FormatGenerationFailureDiagnostics(AnswerDraftRequest? request, Exception ex)
    {
        if (request is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("回答生成診断");
        builder.AppendLine($"使用モデル: {ValueOrUnset(request.Settings.LlmProvider.ChatModel)}");
        builder.AppendLine($"timeout seconds: {request.Settings.LlmProvider.TimeoutSeconds}");
        builder.AppendLine($"max output tokens: {request.Settings.LlmProvider.MaxOutputTokens}");
        builder.AppendLine($"context window tokens: {request.Settings.LlmProvider.ContextWindowTokens}");
        AppendPromptDiagnostics(builder, request);
        builder.AppendLine($"evidence count: {request.Sources.Count}");
        builder.AppendLine($"think:false を送ったか: {(request.Settings.DisableThinking ? "yes" : "no")}");
        builder.AppendLine($"content returned: no");
        builder.AppendLine($"thinking returned: {(ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase) ? "yes" : "unknown")}");
        builder.AppendLine($"done_reason: (取得不可)");
        builder.AppendLine($"error: {FormatExceptionForUi(ex)}");
        return builder.ToString();
    }

    private static string FormatGenerationNoEvidenceSkippedDiagnostics(AnswerDraftRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("回答生成診断");
        builder.AppendLine("結果: LLM呼び出しスキップ");
        builder.AppendLine("理由: 根拠0件時は生成しない設定がON、かつ LLM送信予定の根拠 = 0");
        builder.AppendLine($"使用モデル: {ValueOrUnset(request.Settings.LlmProvider.ChatModel)}");
        builder.AppendLine($"timeout seconds: {request.Settings.LlmProvider.TimeoutSeconds}");
        builder.AppendLine($"max output tokens: {request.Settings.LlmProvider.MaxOutputTokens}");
        builder.AppendLine($"context window tokens: {request.Settings.LlmProvider.ContextWindowTokens}");
        AppendPromptDiagnostics(builder, request);
        builder.AppendLine($"evidence count: {request.Sources.Count}");
        builder.AppendLine($"skip generation when no evidence: {(request.Settings.SkipGenerationWhenNoEvidence ? "on" : "off")}");
        builder.AppendLine($"TopN fallback: {(request.Settings.EnableTopNFallback ? "on" : "off")}");
        return builder.ToString();
    }

    private static string FormatGenerationSuccessDiagnostics(AnswerDraftRequest request, AnswerDraftResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("回答生成診断");
        builder.AppendLine($"使用モデル: {ValueOrUnset(request.Settings.LlmProvider.ChatModel)}");
        builder.AppendLine($"timeout seconds: {request.Settings.LlmProvider.TimeoutSeconds}");
        builder.AppendLine($"max output tokens: {request.Settings.LlmProvider.MaxOutputTokens}");
        builder.AppendLine($"context window tokens: {request.Settings.LlmProvider.ContextWindowTokens}");
        AppendPromptDiagnostics(builder, request);
        builder.AppendLine($"evidence count: {request.Sources.Count}");
        builder.AppendLine($"think:false を送ったか: {(request.Settings.DisableThinking ? "yes" : "no")}");
        builder.AppendLine($"OfficialDoc will send: {request.Sources.Count(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))}");
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("warnings:");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static string FormatGenerationSkippedDiagnostics(AnswerDraftRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("回答生成診断");
        builder.AppendLine("結果: LLM呼び出しスキップ");
        builder.AppendLine("理由: FreshnessSensitive=true かつ OfficialDoc will send = 0");
        builder.AppendLine($"使用モデル: {ValueOrUnset(request.Settings.LlmProvider.ChatModel)}");
        builder.AppendLine($"timeout seconds: {request.Settings.LlmProvider.TimeoutSeconds}");
        builder.AppendLine($"max output tokens: {request.Settings.LlmProvider.MaxOutputTokens}");
        builder.AppendLine($"context window tokens: {request.Settings.LlmProvider.ContextWindowTokens}");
        AppendPromptDiagnostics(builder, request);
        builder.AppendLine($"evidence count: {request.Sources.Count}");
        builder.AppendLine($"OfficialDoc will send: {request.Sources.Count(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))}");
        builder.AppendLine($"Manual will send: {request.Sources.Count(static source => string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase))}");
        builder.AppendLine($"PastCaseNote will send: {request.Sources.Count(static source => string.Equals(source.SourceType, "PastCaseNote", StringComparison.OrdinalIgnoreCase))}");
        if (request.InquiryFocus?.TargetVersions.Count > 0)
        {
            builder.AppendLine($"Detected target version: {string.Join(", ", request.InquiryFocus.TargetVersions)}");
        }

        return builder.ToString();
    }

    private static void AppendPromptDiagnostics(StringBuilder builder, AnswerDraftRequest request)
    {
        var diagnostics = new PromptBuilder().Build(request).Diagnostics;
        builder.AppendLine($"Configured max prompt chars: {diagnostics.ConfiguredMaxPromptChars}");
        builder.AppendLine($"Final prompt chars: {diagnostics.FinalPromptChars}");
        builder.AppendLine($"System chars: {diagnostics.SystemChars}");
        builder.AppendLine($"Inquiry chars: {diagnostics.InquiryChars}");
        builder.AppendLine($"Evidence chars: {diagnostics.EvidenceChars}");
        builder.AppendLine($"Evidence count: {diagnostics.EvidenceCount}");
        AppendFactDiagnostics(builder, request.FactResolution);
    }

    private static void AppendFactDiagnostics(StringBuilder builder, FactResolutionResult? factResolution)
    {
        if (factResolution is null)
        {
            builder.AppendLine("QuestionType: (未分類)");
            builder.AppendLine("LLM prompt uses ResolvedFacts: no");
            return;
        }

        builder.AppendLine($"QuestionType: {(factResolution.Classification.QuestionTypes.Count == 0 ? "(未分類)" : string.Join(", ", factResolution.Classification.QuestionTypes))}");
        builder.AppendLine($"CurrentInstalledVersion: {ValueOrUnset(factResolution.Classification.CurrentInstalledVersion)}");
        builder.AppendLine($"RequestedFacts: {(factResolution.Classification.RequestedFacts.Count == 0 ? "-" : string.Join(", ", factResolution.Classification.RequestedFacts))}");
        builder.AppendLine($"AnswerReadiness: {factResolution.AnswerReadiness}");
        builder.AppendLine($"ResolvedFacts count: {factResolution.ResolvedFacts.Count}");
        builder.AppendLine($"CandidateFacts count: {factResolution.CandidateFacts.Count}");
        builder.AppendLine($"Conflicts: {factResolution.Conflicts.Count}");
        builder.AppendLine($"Crawler conflicts: {factResolution.CrawlerConflicts.Count}");
        builder.AppendLine($"MissingFacts: {(factResolution.MissingFacts.Count == 0 ? "-" : string.Join(", ", factResolution.MissingFacts))}");
        builder.AppendLine($"LLM prompt uses ResolvedFacts: {(factResolution.LlmPromptUsesResolvedFacts ? "yes" : "no")}");
        var curatedFacts = factResolution.ResolvedFacts
            .Where(static fact => string.Equals(fact.SourceType, "Curated", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (curatedFacts.Count > 0)
        {
            builder.AppendLine("CuratedFacts:");
            builder.AppendLine($"- CxSAST latest: {FindFactValue(curatedFacts, FactKeys.LatestSastVersion)}");
            builder.AppendLine($"- EP latest: {FindFactValue(curatedFacts, FactKeys.LatestEnginePackVersion)}");
            builder.AppendLine($"- HF latest: {FindFactValue(curatedFacts, FactKeys.LatestHotfixVersion)}");
            builder.AppendLine("ResolvedFacts source: CuratedFactCatalog");
        }

        if (factResolution.CrawlerConflicts.Count > 0)
        {
            builder.AppendLine("Crawler conflict details:");
            foreach (var conflict in factResolution.CrawlerConflicts.Take(12))
            {
                builder.AppendLine($"- {conflict}");
            }
        }

        if (factResolution.ResolvedFacts.Count > 0)
        {
            builder.AppendLine("ResolvedFacts:");
            foreach (var fact in factResolution.ResolvedFacts)
            {
                builder.AppendLine($"- {fact.Key} = {fact.Value} / {fact.Status} / {fact.Confidence} / {ValueOrUnset(fact.SourceType)}");
            }
        }
    }

    private static string FindFactValue(IReadOnlyList<ResolvedFact> facts, string key)
    {
        return facts.FirstOrDefault(fact => string.Equals(fact.Key, key, StringComparison.OrdinalIgnoreCase))?.Value ?? "(未設定)";
    }

    private static string NormalizeProvider(string? provider)
    {
        return string.Equals(provider?.Trim(), "Ollama", StringComparison.OrdinalIgnoreCase)
            ? "Ollama"
            : "Fake";
    }

    private static string FormatDraftProviderStatus(
        string provider,
        string? model,
        bool usedRealLlm,
        int usedEvidenceCount,
        bool isSuccess)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"使用Provider: {provider}");
        builder.AppendLine($"使用Model: {ValueOrUnset(model)}");
        builder.AppendLine($"実LLM接続: {(usedRealLlm ? "はい" : "いいえ")}");
        builder.AppendLine($"使用した根拠件数: {usedEvidenceCount}");
        builder.AppendLine($"結果: {(isSuccess ? "成功" : "失敗")}");
        return builder.ToString();
    }

    private static string FormatIndexBuildResult(
        AiCaseIndexBuildResult result,
        string? productName,
        string? targetCloseFolder)
    {
        var builder = new StringBuilder();
        builder.AppendLine("過去案件インデックス作成結果");
        builder.AppendLine($"製品名: {ValueOrUnset(productName)}");
        builder.AppendLine($"Index file: {result.IndexFilePath}");
        builder.AppendLine($"Answer pair index: {result.AnswerPairIndexFilePath}");
        builder.AppendLine($"Cases: {result.IndexedCaseCount}");
        builder.AppendLine($"Notes/chunks: {result.IndexedNoteCount}");
        builder.AppendLine($"Question/answer pairs: {result.IndexedAnswerPairCount}");
        builder.AppendLine($"Errors: {result.ErrorCount}");
        builder.AppendLine($"Warnings: {result.Warnings.Count}");
        builder.AppendLine($"対象CloseFolder: {ValueOrUnset(targetCloseFolder)}");
        builder.AppendLine($"ケースフォルダ走査数: {result.ScannedCaseFolderCount}");
        builder.AppendLine($"対象ノートファイル数: {result.ScannedNoteFileCount}");
        builder.AppendLine($"空ファイルスキップ数: {result.EmptyNoteSkippedCount}");
        builder.AppendLine($"サポート番号抽出成功数: {result.SupportNumberExtractedCount}");
        builder.AppendLine($"サポート番号未設定数: {result.MissingSupportNumberCount}");
        builder.AppendLine($"ノート種別抽出成功数: {result.NoteKindExtractedCount}");
        builder.AppendLine($"ノート種別Unknown数: {result.UnknownNoteKindCount}");

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings:");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        var samples = ReadCaseIndexSamples(result.IndexFilePath);
        if (samples.Count > 0)
        {
            builder.AppendLine("代表サンプル:");
            foreach (var sample in samples)
            {
                builder.AppendLine($"- SupportNumber: {ValueOrUnset(sample.SupportNumber)}");
                builder.AppendLine($"  CompanyName: {ValueOrUnset(sample.CompanyName)}");
                builder.AppendLine($"  Status: {ValueOrUnset(sample.Status)}");
                builder.AppendLine($"  NoteKind: {ValueOrUnset(sample.NoteKind)}");
                builder.AppendLine($"  Title: {ValueOrUnset(sample.Title)}");
                builder.AppendLine($"  FilePath: {ValueOrUnset(sample.NoteFilePath)}");
            }
        }

        return builder.ToString();
    }

    private static string FormatManualIndexBuildResult(
        AiManualIndexBuildResult result,
        string? productName,
        IReadOnlyList<string> targetManualFolders)
    {
        var builder = new StringBuilder();
        builder.AppendLine("マニュアルインデックス作成結果");
        builder.AppendLine($"製品名: {ValueOrUnset(productName)}");
        builder.AppendLine($"Index file: {result.IndexFilePath}");
        builder.AppendLine($"Files: {result.IndexedFileCount}");
        builder.AppendLine($"Chunks: {result.IndexedChunkCount}");
        builder.AppendLine($"Errors: {result.ErrorCount}");
        builder.AppendLine($"Warnings: {result.Warnings.Count}");
        builder.AppendLine("対象ManualFolders:");
        foreach (var folder in targetManualFolders.Where(static folder => !string.IsNullOrWhiteSpace(folder)))
        {
            builder.AppendLine($"- {folder}");
        }
        builder.AppendLine($"走査ファイル総数: {result.ScannedFileCount}");
        builder.AppendLine($"取り込み対象候補(.txt/.md/.pdf/.docx/.xlsx/.pptx/.html/.csv/.tsv等): {result.SupportedFileCount}");
        builder.AppendLine($"取り込み済み: {result.IndexedFileCount}");
        builder.AppendLine($"内容判定で除外: {result.ContentExcludedFileCount}");
        builder.AppendLine($"空ファイルスキップ: {result.EmptyFileSkippedCount}");
        builder.AppendLine($"未対応ドキュメント形式: {result.UnsupportedDocumentFileCount}");
        builder.AppendLine($"対象外バイナリ/アーカイブ: {result.OutOfScopeFileCount}");
        builder.AppendLine($"その他未対応: {result.OtherUnsupportedFileCount}");
        builder.AppendLine($"未対応ファイル総数: {result.UnsupportedFileCount}");
        builder.AppendLine($"読み取り失敗: {result.ReadFailureCount}");
        builder.AppendLine($"重複ファイルスキップ: {result.DuplicateFileSkippedCount}");
        builder.AppendLine("PDF/DOCX/XLSX/PPTX/HTML/CSV/TSVはテキスト抽出して取り込みます。");
        builder.AppendLine("PNG/JPG等の画像OCR、旧Office形式(.doc/.xls/.ppt)は現在未対応です。");
        builder.AppendLine("ZIPの中身は現在確認しません。");
        builder.AppendLine("EXE/RUN/DB/PDB/BAK/ZIPは検索対象外です。");

        if (result.UnsupportedExtensionCounts.Count > 0)
        {
            builder.AppendLine("未対応拡張子内訳:");
            foreach (var item in result.UnsupportedExtensionCounts.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"{item.Key}: {item.Value}");
            }
        }

        if (result.UnsupportedDocumentExtensionCounts.Count > 0)
        {
            builder.AppendLine("未対応ドキュメント形式内訳:");
            foreach (var item in result.UnsupportedDocumentExtensionCounts.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"{item.Key}: {item.Value}");
            }
        }

        if (result.OutOfScopeExtensionCounts.Count > 0)
        {
            builder.AppendLine("対象外バイナリ/アーカイブ内訳:");
            foreach (var item in result.OutOfScopeExtensionCounts.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"{item.Key}: {item.Value}");
            }
        }

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings:");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        var samples = ReadManualIndexSamples(result.IndexFilePath);
        if (samples.Count > 0)
        {
            builder.AppendLine("代表サンプル:");
            foreach (var sample in samples)
            {
                builder.AppendLine($"- Title: {ValueOrUnset(sample.Title)}");
                builder.AppendLine($"  SectionTitle: {ValueOrUnset(sample.SectionTitle)}");
                builder.AppendLine($"  File path: {ValueOrUnset(sample.FilePath)}");
            }
        }

        return builder.ToString();
    }

    private static string FormatOfficialDocumentIndexBuildResult(AiOfficialDocumentIndexBuildResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("公式URLインデックス作成結果");
        builder.AppendLine($"製品名: {ValueOrUnset(result.ProductName)}");
        builder.AppendLine($"Index file: {result.IndexFilePath}");
        builder.AppendLine($"Seed URL数: {result.SourceUrlCount}");
        builder.AppendLine($"探索URL数: {result.DiscoveredUrlCount}");
        builder.AppendLine($"取得成功: {result.FetchSuccessCount}");
        builder.AppendLine($"取得失敗: {result.FetchFailureCount}");
        builder.AppendLine($"スキップ: {result.SkippedUrlCount}");
        builder.AppendLine($"Chunks: {result.IndexedChunkCount}");
        builder.AppendLine($"MaxDepth: {result.MaxDepth}");
        builder.AppendLine($"MaxPages: {result.MaxPages}");
        builder.AppendLine($"RequestDelayMs: {result.RequestDelayMs}");
        builder.AppendLine($"FetchTimeoutSeconds: {result.FetchTimeoutSeconds}");
        if (result.IndexedChunkCount == 0)
        {
            builder.AppendLine("警告: Chunks=0 のため、公式URLインデックスは回答根拠として使えません。HTML取得/抽出を確認してください。");
        }

        builder.AppendLine($"Warnings: {result.Warnings.Count}");
        builder.AppendLine("発見URL:");
        foreach (var url in result.DiscoveredUrls.Take(30))
        {
            builder.AppendLine($"- {url}");
        }

        builder.AppendLine("取得URL:");
        foreach (var url in result.RetrievedUrls)
        {
            builder.AppendLine($"- {url}");
        }

        if (result.ImportantPageUrls.Count > 0)
        {
            builder.AppendLine("重要ページ候補:");
            foreach (var url in result.ImportantPageUrls)
            {
                builder.AppendLine($"- {url}");
            }
        }

        if (result.FailedUrls.Count > 0)
        {
            builder.AppendLine("失敗URL:");
            foreach (var url in result.FailedUrls.Take(30))
            {
                builder.AppendLine($"- {url}");
            }
        }

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings:");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        var samples = ReadOfficialDocumentIndexSamples(result.IndexFilePath);
        if (samples.Count > 0)
        {
            builder.AppendLine("代表サンプル:");
            foreach (var sample in samples)
            {
                builder.AppendLine($"- {ValueOrUnset(sample.Title)}");
                builder.AppendLine($"  SectionTitle: {ValueOrUnset(sample.SectionTitle)}");
                builder.AppendLine($"  Url: {ValueOrUnset(sample.Url)}");
                builder.AppendLine($"  RetrievedAt: {sample.RetrievedAt:O}");
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<AiIndexedNote> ReadCaseIndexSamples(string indexFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(indexFilePath) || !File.Exists(indexFilePath))
            {
                return [];
            }

            using var stream = File.OpenRead(indexFilePath);
            var document = JsonSerializer.Deserialize<AiIndexDocument>(stream);
            return document?.Notes.Take(3).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<AiIndexedManual> ReadManualIndexSamples(string indexFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(indexFilePath) || !File.Exists(indexFilePath))
            {
                return [];
            }

            using var stream = File.OpenRead(indexFilePath);
            var document = JsonSerializer.Deserialize<AiManualIndexDocument>(stream);
            return document?.Manuals.Take(3).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<AiIndexedOfficialDocument> ReadOfficialDocumentIndexSamples(string indexFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(indexFilePath) || !File.Exists(indexFilePath))
            {
                return [];
            }

            using var stream = File.OpenRead(indexFilePath);
            var document = JsonSerializer.Deserialize<AiOfficialDocumentIndexDocument>(stream);
            return document?.Documents.Take(3).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string FormatSearchResults(IReadOnlyList<SearchSource> sources)
    {
        if (sources.Count == 0)
        {
            return "No matching past case notes found.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Results: {sources.Count}");
        foreach (var source in sources)
        {
            builder.AppendLine($"- [{source.Score ?? 0:0.000}] {source.Title}");
            builder.AppendLine($"  SourceType: {source.SourceType}");
            if (!string.IsNullOrWhiteSpace(source.ProductName))
            {
                builder.AppendLine($"  ProductName: {source.ProductName}");
            }

            if (!string.IsNullOrWhiteSpace(source.SupportNumber))
            {
                builder.AppendLine($"  SupportNumber: {source.SupportNumber}");
            }

            if (source.MatchedTerms.Count > 0)
            {
                builder.AppendLine($"  Matched terms: {string.Join(", ", source.MatchedTerms)}");
            }

            if (!string.IsNullOrWhiteSpace(source.QueryCoverage))
            {
                builder.AppendLine($"  Coverage: {source.QueryCoverage}");
            }

            if (!string.IsNullOrWhiteSpace(source.ScoreBreakdown))
            {
                builder.AppendLine($"  Score: {source.ScoreBreakdown}");
            }

            if (!string.IsNullOrWhiteSpace(source.FilePath))
            {
                builder.AppendLine($"  File: {source.FilePath}");
            }

            if (!string.IsNullOrWhiteSpace(source.Url))
            {
                builder.AppendLine($"  Url: {source.Url}");
            }

            if (source.RetrievedAt is not null)
            {
                builder.AppendLine($"  RetrievedAt: {source.RetrievedAt:O}");
            }

            builder.AppendLine($"  {source.Text}");
        }

        return builder.ToString();
    }

    private void MarkUsedSources(IReadOnlyList<SearchSource> usedSources)
    {
        var usedIds = usedSources
            .Select(static source => source.SourceId ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in SearchResults)
        {
            item.WasUsedInLastDraft = usedIds.Contains(item.SourceId);
        }
    }

    private string FormatEvidenceSummary(SearchSourceSummary summary)
    {
        var selection = summary.Selection;
        var builder = new StringBuilder();
        builder.AppendLine($"SourceTypeフィルタ: {SourceTypeFilter}");
        builder.AppendLine($"検索結果: {selection.SearchResultCount}件");
        builder.AppendLine($"表示中: {summary.FilteredCount}件");
        builder.AppendLine($"SourceTypeフィルタで非表示: {summary.HiddenBySourceTypeFilterCount}件");
        builder.AppendLine($"表示最小スコア: {summary.MinimumDisplayScore:0.000}");
        builder.AppendLine($"表示最小スコアで非表示: {summary.HiddenByMinimumScoreCount}件");
        builder.AppendLine($"自動選択の最小スコア: {summary.AutoSelectMinimumScore:0.000}");
        builder.AppendLine($"自動選択スコア未満: {summary.BelowAutoSelectScoreCount}件");
        builder.AppendLine($"選択中: {selection.SelectedCount}件");
        builder.AppendLine($"LLM送信予定: {selection.Sources.Count}件");
        builder.AppendLine($"スコアによりLLM送信対象外: {selection.ExcludedByScoreCount}件");
        builder.AppendLine($"上限超過により除外: {selection.ExcludedSelectedCount}件");
        builder.AppendLine($"PastCaseNote選択: {selection.PastCaseNoteSelectedCount}件");
        builder.AppendLine($"Manual選択: {selection.ManualSelectedCount}件");
        builder.AppendLine($"OfficialDoc選択: {selection.OfficialDocSelectedCount}件");
        builder.AppendLine($"PastCaseNote送信予定: {selection.PastCaseNoteSendCount}件");
        builder.AppendLine($"Manual送信予定: {selection.ManualSendCount}件");
        builder.AppendLine($"OfficialDoc送信予定: {selection.OfficialDocSendCount}件");
        builder.AppendLine($"最大根拠件数: {selection.MaxEvidenceItems}件");
        if (lastInquiryFocus?.IsFreshnessSensitive == true)
        {
            builder.AppendLine("鮮度重要質問: はい");
            builder.AppendLine($"理由: {lastInquiryFocus.FreshnessReason}");
            builder.AppendLine("推奨根拠: OfficialDoc");
            builder.AppendLine("過去案件のみでの断定回答: 禁止");
            if (selection.Sources.All(static source => !string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase)))
            {
                builder.AppendLine("警告: 公式ドキュメント根拠が見つかりません。メーカー公式情報を確認してから回答してください。");
            }
        }
        else
        {
            builder.AppendLine("鮮度重要質問: いいえ");
        }

        builder.AppendLine(selection.TopNFallbackApplied
            ? $"通常選択0件のため、TopN fallbackで{selection.Sources.Count}件を送信予定です。"
            : selection.WasLimited
            ? $"スコア上位{selection.Sources.Count}件のみ送信します。"
            : "選択中の根拠はすべて送信予定です。");
        if (!string.IsNullOrWhiteSpace(selection.Warning))
        {
            builder.AppendLine($"警告: {selection.Warning}");
        }

        return builder.ToString();
    }

    private static string FormatUsedSources(IReadOnlyList<SearchSource> sources)
    {
        if (sources.Count == 0)
        {
            return "LLMへ送信された根拠はありません。";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Used evidence count: {sources.Count}");
        foreach (var source in sources)
        {
            builder.AppendLine($"- [{source.Score ?? 0:0.000}] {source.Title}");
            builder.AppendLine($"  SourceId: {source.SourceId}");
            builder.AppendLine($"  SourceType: {source.SourceType}");
            if (!string.IsNullOrWhiteSpace(source.ProductName))
            {
                builder.AppendLine($"  ProductName: {source.ProductName}");
            }

            if (!string.IsNullOrWhiteSpace(source.SupportNumber))
            {
                builder.AppendLine($"  SupportNumber: {source.SupportNumber}");
            }

            if (source.MatchedTerms.Count > 0)
            {
                builder.AppendLine($"  Matched terms: {string.Join(", ", source.MatchedTerms)}");
            }

            if (!string.IsNullOrWhiteSpace(source.QueryCoverage))
            {
                builder.AppendLine($"  Coverage: {source.QueryCoverage}");
            }

            if (!string.IsNullOrWhiteSpace(source.ScoreBreakdown))
            {
                builder.AppendLine($"  Score: {source.ScoreBreakdown}");
            }

            if (!string.IsNullOrWhiteSpace(source.FilePath))
            {
                builder.AppendLine($"  File: {source.FilePath}");
            }

            if (!string.IsNullOrWhiteSpace(source.Url))
            {
                builder.AppendLine($"  Url: {source.Url}");
            }

            if (source.RetrievedAt is not null)
            {
                builder.AppendLine($"  RetrievedAt: {source.RetrievedAt:O}");
            }

            builder.AppendLine($"  Excerpt: {BuildExcerpt(source.Text, 300)}");
        }

        return builder.ToString();
    }

    private static string FormatInquiryFocusSummary(InquiryFocus focus)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"鮮度重要質問: {(focus.IsFreshnessSensitive ? "はい" : "いいえ")}");
        if (!string.IsNullOrWhiteSpace(focus.FreshnessReason))
        {
            builder.AppendLine($"理由: {focus.FreshnessReason}");
        }

        builder.AppendLine($"焦点本文: {BuildExcerpt(focus.FocusText, 360)}");
        builder.AppendLine($"対象バージョン: {(focus.TargetVersions.Count == 0 ? "-" : string.Join(", ", focus.TargetVersions))}");
        builder.AppendLine($"重要語: {(focus.ImportantTerms.Count == 0 ? "-" : string.Join(", ", focus.ImportantTerms.Take(20)))}");
        builder.AppendLine($"除外語: {(focus.ExcludedTerms.Count == 0 ? "-" : string.Join(", ", focus.ExcludedTerms))}");
        return builder.ToString();
    }

    private static string PrependWarning(string currentWarnings, string warning)
    {
        if (string.IsNullOrWhiteSpace(currentWarnings) || currentWarnings == "(縺ｪ縺・")
        {
            return $"- {warning}";
        }

        return $"- {warning}{Environment.NewLine}{currentWarnings}";
    }

    private static int SafeLength(string? value)
    {
        return value?.Length ?? 0;
    }

    private static string FormatExceptionForUi(Exception exception)
    {
        var message = exception.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (message.Length > 240)
        {
            message = message[..240] + "...";
        }

        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {message}";
    }

    private void OpenSelectedSourceFile()
    {
        var filePath = SelectedSearchResult?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            StatusMessage = "No search result file is selected.";
            return;
        }

        if (!File.Exists(filePath))
        {
            StatusMessage = "Selected source file does not exist.";
            return;
        }

        OpenShellPath(filePath);
    }

    private void OpenSelectedSourceFolder()
    {
        var filePath = SelectedSearchResult?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            StatusMessage = "No search result file is selected.";
            return;
        }

        var folderPath = File.Exists(filePath)
            ? Path.GetDirectoryName(filePath)
            : filePath;

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            StatusMessage = "Selected source folder does not exist.";
            return;
        }

        OpenShellPath(folderPath);
    }

    private void OpenShellPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
            StatusMessage = "Opened selected source path.";
        }
        catch (Exception ex)
        {
            ErrorText = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Failed to open selected source path.";
        }
    }

    private static string BuildExcerpt(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }

    private string BuildFullDraftText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# お客様向け回答案");
        builder.AppendLine(CustomerReplyDraft);
        builder.AppendLine();
        builder.AppendLine("# 社内メモ");
        builder.AppendLine(InternalMemo);
        builder.AppendLine();
        builder.AppendLine("# 要確認事項");
        builder.AppendLine(NeedConfirmationsText);
        builder.AppendLine();
        builder.AppendLine("# 参照根拠");
        builder.AppendLine(EvidenceText);
        builder.AppendLine();
        builder.AppendLine("# 信頼度");
        builder.AppendLine(ConfidenceText);
        builder.AppendLine();
        builder.AppendLine("# 警告");
        builder.AppendLine(WarningsText);
        return builder.ToString();
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (settingsLoaded &&
            !isApplyingSettings &&
            propertyName is not null &&
            AutoSavedProperties.Contains(propertyName))
        {
            ScheduleAutoSave();
        }
    }

    private void ScheduleAutoSave()
    {
        autoSaveCancellation?.Cancel();
        autoSaveCancellation?.Dispose();
        autoSaveCancellation = new CancellationTokenSource();
        var token = autoSaveCancellation.Token;
        var snapshot = BuildSettings();
        _ = SaveSettingsAfterDelayAsync(snapshot, token);
    }

    private async Task SaveSettingsAfterDelayAsync(
        AiAssistantSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                await loggerFactory(settings.AiDataFolder).LogWarningAsync(
                    $"Settings auto-save failed. {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }
        }
    }

    private void CopyText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "クリップボードにコピーしました。";
        }
    }

    private void SelectCodexExecutable()
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Codex実行ファイルを選択",
            Filter = "Codex実行ファイル (codex.exe)|codex.exe|実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            CodexExecutablePath = dialog.FileName;
            StatusMessage = "Codex実行ファイルを設定しました。接続テストで確認してください。";
        }
    }

    private static void SelectFolder(Action<string> setter)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = "フォルダを選択してください。",
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            setter(dialog.SelectedPath);
        }
    }

    private static bool IsHttpOrHttpsUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private string GetCurrentSearchIndexFolder()
    {
        var selectedProduct = GetSelectedProductSettings();
        return selectedProduct is null
            ? EffectiveAiIndexFolder()
            : productScopedIndexService.GetProductIndexFolder(EffectiveAiIndexFolder(), selectedProduct.ProductName);
    }

    private string EffectiveAiDataFolder()
    {
        return string.IsNullOrWhiteSpace(AiDataFolder) ? DefaultAiDataFolder() : AiDataFolder;
    }

    private string EffectiveAiIndexFolder()
    {
        return string.IsNullOrWhiteSpace(AiIndexFolder) ? DefaultAiIndexFolder() : AiIndexFolder;
    }

    private static string DefaultAiDataFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SupportCaseManager",
            "ai-data");
    }

    private static string DefaultAiIndexFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SupportCaseManager",
            "ai-index");
    }

    private sealed record SearchSourceSelectionState(
        bool IsSelected,
        bool IsManuallySelected,
        bool IsManuallyExcluded);
}
