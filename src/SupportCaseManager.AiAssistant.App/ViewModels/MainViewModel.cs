using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
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
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.AiAssistant.App.Appearance;
using SupportCaseManager.AiAssistant.App.Launch;
using SupportCaseManager.AiAssistant.App.Llm;
using WinForms = System.Windows.Forms;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
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
    private string chatModel = "llama3.1";
    private string embeddingModel = "nomic-embed-text";
    private double temperature = 0.2;
    private int maxOutputTokens = 2048;
    private int timeoutSeconds = 120;
    private int maxEvidenceItems = 8;
    private int maxPromptChars = 24000;
    private bool enableCloudLlm;
    private bool maskSensitiveDataForCloud = true;
    private bool disableThinking = true;
    private string caseFolderPath = string.Empty;
    private string productName = "製品A";
    private string companyName = "株式会社サンプル";
    private string supportNumber = "00001234";
    private string status = "対応中";
    private string receptionDate = "2026-06-02";
    private NoteSnapshot? selectedNote;
    private string inquiryText = "エラーの原因と対応方針を確認したいです。";
    private string additionalInstruction = "丁寧で簡潔に回答してください。";
    private int evidenceCount;
    private int promptApproxChars;
    private string customerReplyDraft = "まだ生成されていません。";
    private string internalMemo = string.Empty;
    private string needConfirmationsText = string.Empty;
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
    private bool isBusy;
    private bool isUpdatingPromptSummary;

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
        IAppAppearanceService appearanceService)
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

        LoadSettingsCommand = new AsyncRelayCommand(LoadSettingsAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        CheckOllamaConnectionCommand = new AsyncRelayCommand(CheckOllamaConnectionAsync);
        RunOllamaProductionMiniTestCommand = new AsyncRelayCommand(RunOllamaProductionMiniTestAsync);
        SelectAiDataFolderCommand = new RelayCommand(() => SelectFolder(value => AiDataFolder = value));
        SelectAiIndexFolderCommand = new RelayCommand(() => SelectFolder(value => AiIndexFolder = value));
        SelectBaseFolderCommand = new RelayCommand(() => SelectFolder(value => BaseFolder = value));
        SelectCloseFolderCommand = new RelayCommand(() => SelectFolder(value => CloseFolder = value));
        SelectManualFolderCommand = new RelayCommand(() => SelectFolder(value => ManualFolder = value));
        SelectSupportToolSettingsFileCommand = new RelayCommand(SelectSupportToolSettingsFile);
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
            if (SetProperty(ref chatModel, value))
            {
                UpdateModelRecommendationText();
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
        private set => SetProperty(ref customerReplyDraft, value);
    }

    public string InternalMemo
    {
        get => internalMemo;
        private set => SetProperty(ref internalMemo, value);
    }

    public string NeedConfirmationsText
    {
        get => needConfirmationsText;
        private set => SetProperty(ref needConfirmationsText, value);
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
        private set => SetProperty(ref isBusy, value);
    }

    public string LogFilePath => Path.Combine(EffectiveAiDataFolder(), "logs", "AiAssistant.log");

    public AsyncRelayCommand LoadSettingsCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand CheckOllamaConnectionCommand { get; }
    public AsyncRelayCommand RunOllamaProductionMiniTestCommand { get; }
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
    public RelayCommand ClearInquiryCommand { get; }
    public RelayCommand CopyCustomerReplyCommand { get; }
    public RelayCommand CopyInternalMemoCommand { get; }
    public RelayCommand CopyAllCommand { get; }
    public AsyncRelayCommand SaveDraftCommand { get; }
    public AsyncRelayCommand WriteTestLogCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    public async Task InitializeFromCommandLineAsync(CommandLineOptions options)
    {
        options ??= new CommandLineOptions();

        if (options.Warnings.Count > 0)
        {
            var warningText = string.Join(" ", options.Warnings);
            StatusMessage = warningText;
            await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync($"Command line warning. Count={options.Warnings.Count}");
        }

        if (string.IsNullOrWhiteSpace(options.ContextFilePath))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var settings = await settingsStore.LoadAsync(EffectiveAiDataFolder());
            ApplySettings(settings);

            var context = await launchContextReader.ReadAsync(options.ContextFilePath);
            ApplyLaunchContext(context);

            var caseFolderExists = !string.IsNullOrWhiteSpace(context.CaseFolderPath)
                && Directory.Exists(context.CaseFolderPath);
            var noteFileExists = !string.IsNullOrWhiteSpace(context.NoteFilePath)
                && File.Exists(context.NoteFilePath);

            LastOperationResult = FormatLaunchContextDiagnostic(context, caseFolderExists, noteFileExists);
            ProductKnowledgeStatusText = string.IsNullOrWhiteSpace(context.ProductName)
                ? ProductKnowledgeStatusText
                : $"外部コンテキスト製品: {context.ProductName}";
            StatusMessage = "外部コンテキストを読み込みました。";

            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
                $"Launch context loaded. Source={SanitizeLogToken(context.Source)}; ProductName={SanitizeLogToken(context.ProductName)}; CaseFolderExists={caseFolderExists}; NoteFileExists={noteFileExists}");
        });
    }

    public void ApplyLaunchContext(AiAssistantLaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

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
        SupportNumber = context.SupportNumber;
        Status = context.Status;
        ReceptionDate = context.ReceptionDate?.ToString("yyyy-MM-dd") ?? string.Empty;

        var inquiry = FirstNonWhiteSpace(context.InquiryText, context.SelectedText, context.CurrentNoteText);
        if (!string.IsNullOrWhiteSpace(inquiry))
        {
            InquiryText = inquiry;
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
            var settings = BuildSettings();
            var result = await ollamaConnectionChecker.CheckAsync(settings.LlmProvider, settings.DisableThinking);
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
            var providerSettings = settings.LlmProvider with
            {
                Provider = "Ollama",
                MaxOutputTokens = 100,
            };
            var testSettings = settings with
            {
                LlmProvider = providerSettings,
                DisableThinking = true,
                MaxEvidenceItems = 0,
            };
            const string inquiryText = "テストです。動作確認をお願いします。";
            const string instruction = "以下の問い合わせに対するお客様向け返信を、日本語で2文だけ作成してください。";
            var request = new AnswerDraftRequest
            {
                Case = BuildCurrentCaseContext(),
                InquiryText = inquiryText,
                UserInstruction = instruction,
                InquiryFocus = inquiryFocusExtractor.Extract(inquiryText, BuildCurrentCaseContext()),
                Sources = [],
                Settings = testSettings,
                RequestedAt = DateTimeOffset.Now,
            };
            var promptMessages = new PromptBuilder().Build(request);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var generation = await new LlmClientFactory()
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

            currentCaseContext = await caseContextBuilder.BuildFromCaseFolderAsync(
                CaseFolderPath,
                ProductName,
                BaseFolder,
                CloseFolder);
            ApplyCaseContext(currentCaseContext);
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

            var notes = await noteSnapshotReader.ReadAllAsync(CaseFolderPath);
            ReplaceNotes(notes);
            currentCaseContext = BuildCurrentCaseContext();
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
                    $"Product case index built. Product={selectedProduct.ProductName}; Cases={productResult.IndexedCaseCount}; Notes={productResult.IndexedNoteCount}; Errors={productResult.ErrorCount}; Path={productResult.IndexFilePath}");
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
                $"Index built. Cases={result.IndexedCaseCount}; Notes={result.IndexedNoteCount}; Errors={result.ErrorCount}; Path={result.IndexFilePath}");
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
            var searchLimit = Math.Max(12, Math.Max(1, MaxEvidenceItems) * 2);
            var selectedProduct = GetSelectedProductSettings();
            lastInquiryFocus = inquiryFocusExtractor.Extract(InquiryText, BuildCurrentCaseContext());
            InquiryFocusSummaryText = FormatInquiryFocusSummary(lastInquiryFocus);

            if (selectedProduct is null)
            {
                lastSearchSources = await keywordSearcher.SearchAsync(
                    EffectiveAiIndexFolder(),
                    lastInquiryFocus.FocusText,
                    searchLimit);
                lastManualSearchSources = await manualKeywordSearcher.SearchAsync(
                    EffectiveAiIndexFolder(),
                    lastInquiryFocus.FocusText,
                    searchLimit);
                lastOfficialDocumentSearchSources = [];
            }
            else
            {
                var allSources = await productScopedSearchService.SearchAllAsync(
                    selectedProduct,
                    EffectiveAiIndexFolder(),
                    lastInquiryFocus,
                    searchLimit * 3);
                lastSearchSources = allSources
                    .Where(static source => string.Equals(source.SourceType, "PastCaseNote", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                lastManualSearchSources = allSources
                    .Where(static source => string.Equals(source.SourceType, "Manual", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                lastOfficialDocumentSearchSources = allSources
                    .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var combined = BuildCombinedSearchSources();
            ReplaceSearchResults(combined);
            SearchResultsText = FormatSearchResults(combined);
            UpdatePromptSummary();
            var summary = SearchSourceSummaryBuilder.Build(
                SearchResults,
                SourceTypeFilter,
                MaxEvidenceItems,
                HighScoreThreshold,
                MinimumDisplayScore,
                lastInquiryFocus.IsFreshnessSensitive);
            UpdateOfficialDocDiagnostics(summary);
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

    private async Task GenerateDraftAsync()
    {
        await RunBusyAsync(async () =>
        {
            lastRequest = BuildDraftRequest();
            var provider = NormalizeProvider(lastRequest.Settings.LlmProvider.Provider);
            var model = lastRequest.Settings.LlmProvider.ChatModel;
            lastUsedSources = lastRequest.Sources;
            MarkUsedSources(lastUsedSources);
            UsedSourcesText = FormatUsedSources(lastUsedSources);
            UsedEvidenceCount = lastUsedSources.Count;

            if (ShouldSkipFreshnessWithoutOfficialDoc(lastRequest))
            {
                lastResult = BuildFreshnessNoOfficialDocResult(lastRequest);
                ApplyDraftResult(lastResult);
                GenerationDiagnosticsText = FormatGenerationSkippedDiagnostics(lastRequest);
                DraftProviderStatusText = FormatDraftProviderStatus(provider, model, usedRealLlm: false, usedEvidenceCount: lastUsedSources.Count, isSuccess: true);
                WarningsText = PrependWarning(WarningsText, "鮮度重要な問い合わせですが、OfficialDoc根拠がないためLLM呼び出しをスキップしました。");
                StatusMessage = "OfficialDoc根拠がない鮮度重要問い合わせのため、安全な固定回答案を表示しました。";
                LastOperationResult = $"Draft generation skipped. Reason=FreshnessWithoutOfficialDoc; Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}";
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                    $"Draft generation skipped. Reason=FreshnessWithoutOfficialDoc; Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}; OfficialDoc=0");
                return;
            }

            try
            {
                lastResult = await answerServiceFactory(lastRequest.Settings.LlmProvider).GenerateDraftAsync(lastRequest);
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
                await loggerFactory(EffectiveAiDataFolder()).LogErrorAsync($"Draft generation failed. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}", ex);
                return;
            }

            ApplyDraftResult(lastResult);
            GenerationDiagnosticsText = FormatGenerationSuccessDiagnostics(lastRequest, lastResult);
            if (lastUsedSources.Count == 0)
            {
                WarningsText = PrependWarning(WarningsText, "No selected evidence was passed to the LLM.");
            }

            DraftProviderStatusText = FormatDraftProviderStatus(provider, model, provider == "Ollama", lastUsedSources.Count, isSuccess: true);
            await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync($"Draft generated. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}");
            StatusMessage = provider == "Ollama"
                ? "Ollamaで回答案を生成しました。"
                : "モック回答案を生成しました。";
            LastOperationResult = $"Draft generated. Provider={provider}; Model={model}; Evidence={lastUsedSources.Count}";
        });
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

    private async Task RunBusyAsync(Func<Task> action)
    {
        try
        {
            IsBusy = true;
            ErrorText = string.Empty;
            await action();
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
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySettings(AiAssistantSettings settings)
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
        LlmProvider = string.IsNullOrWhiteSpace(settings.LlmProvider.Provider) ? "Fake" : settings.LlmProvider.Provider;
        OllamaEndpoint = settings.LlmProvider.Endpoint;
        ChatModel = settings.LlmProvider.ChatModel;
        EmbeddingModel = settings.LlmProvider.EmbeddingModel ?? string.Empty;
        Temperature = settings.LlmProvider.Temperature;
        MaxOutputTokens = settings.LlmProvider.MaxOutputTokens;
        TimeoutSeconds = settings.LlmProvider.TimeoutSeconds;
        RefreshProductContextComputedProperties();
    }

    private AiAssistantSettings BuildSettings()
    {
        SynchronizeSelectedProductFromCurrentFields();
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
            LlmProvider = new LlmProviderSettings
            {
                Provider = string.IsNullOrWhiteSpace(this.LlmProvider) ? "Fake" : this.LlmProvider,
                Endpoint = OllamaEndpoint,
                ChatModel = ChatModel,
                EmbeddingModel = string.IsNullOrWhiteSpace(EmbeddingModel) ? null : EmbeddingModel,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens,
                TimeoutSeconds = TimeoutSeconds,
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
        SupportNumber = context.SupportNumber ?? string.Empty;
        Status = context.Status ?? string.Empty;
        ReceptionDate = context.ReceptionDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        ReplaceNotes(context.Notes);
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
            Products.Add(ProductKnowledgeViewModel.FromSettings(product));
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
            string.Equals(item.ProductName, productNameFromContext, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            product = new ProductKnowledgeViewModel
            {
                ProductName = productNameFromContext,
                BaseFolder = context.BaseFolder ?? string.Empty,
                CloseFolder = context.CloseFolder ?? string.Empty,
                IsEnabled = true,
            };
            Products.Add(product);
            ProductKnowledgeStatusText = $"外部Context製品を新規作成し、検索対象にしました: {productNameFromContext}";
        }
        else
        {
            ProductKnowledgeStatusText = $"外部Context製品を検索対象にしました: {productNameFromContext}";
        }

        SelectedProductKnowledge = product;
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

    private static int GetSourcePriority(string? sourceType, bool freshnessSensitive)
    {
        if (freshnessSensitive)
        {
            return sourceType switch
            {
                "OfficialDoc" => 0,
                "Manual" => 1,
                "PastCaseNote" => 2,
                _ => 3,
            };
        }

        return sourceType switch
        {
            "Manual" => 0,
            "OfficialDoc" => 1,
            "PastCaseNote" => 2,
            _ => 3,
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
        return lastOfficialDocumentSearchSources
            .Concat(lastManualSearchSources)
            .Concat(lastSearchSources)
            .OrderBy(source => GetSourcePriority(source.SourceType, lastInquiryFocus?.IsFreshnessSensitive == true))
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
        var inquiryFocus = lastInquiryFocus ?? inquiryFocusExtractor.Extract(InquiryText, caseContext);
        var settings = BuildSettings();
        return new AnswerDraftRequest
        {
            Case = caseContext,
            InquiryText = InquiryText,
            InquiryFocus = inquiryFocus,
            UserInstruction = AdditionalInstruction,
            Sources = sources,
            FactResolution = new FactResolver().Resolve(
                effectiveProductName,
                settings.AiIndexFolder,
                InquiryText,
                inquiryFocus),
            Settings = settings,
            RequestedAt = DateTimeOffset.Now,
        };
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
            SupportNumber = SupportNumber,
            Status = Status,
            ReceptionDate = DateOnly.TryParse(ReceptionDate, out var parsedDate) ? parsedDate : null,
            Notes = Notes.ToList(),
        };
    }

    private string ResolveEffectiveProductName(IReadOnlyList<SearchSource>? sources = null)
    {
        if (!string.IsNullOrWhiteSpace(SelectedProductKnowledge?.ProductName))
        {
            return SelectedProductKnowledge.ProductName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ProductName) &&
            !string.Equals(ProductName.Trim(), "製品A", StringComparison.OrdinalIgnoreCase))
        {
            return ProductName.Trim();
        }

        var sourceProductName = sources?
            .Select(static source => source.ProductName)
            .FirstOrDefault(static productName => !string.IsNullOrWhiteSpace(productName));
        if (!string.IsNullOrWhiteSpace(sourceProductName))
        {
            return sourceProductName.Trim();
        }

        if (InquiryText.Contains("Checkmarx", StringComparison.OrdinalIgnoreCase) ||
            InquiryText.Contains("CxSAST", StringComparison.OrdinalIgnoreCase) ||
            InquiryText.Contains("ＣｘＳＡＳＴ", StringComparison.OrdinalIgnoreCase))
        {
            return "Checkmarx";
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
            lastInquiryFocus?.IsFreshnessSensitive == true);
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
        EvidenceText = result.Evidence.Count == 0
            ? "(なし)"
            : string.Join(Environment.NewLine, result.Evidence.Select(item => $"- {item.SourceId} {item.Title} ({item.Relevance:0.00}) {item.Excerpt}"));
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
                lastInquiryFocus?.IsFreshnessSensitive == true);
            ApplySelectionSummary(summary);
            UpdateOfficialDocDiagnostics(summary);
            EvidenceCount = summary.Selection.Sources.Count;
            PromptApproxChars = SafeLength(InquiryText)
                + SafeLength(AdditionalInstruction)
                + summary.Selection.Sources.Sum(static source => SafeLength(source.Text))
                + Notes.Sum(static note => SafeLength(note.Text));
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
        builder.AppendLine($"model: {settings.ChatModel}");
        builder.AppendLine($"configured timeout seconds: {settings.TimeoutSeconds}");
        builder.AppendLine($"elapsed seconds: {elapsed.TotalSeconds:0.0}");
        builder.AppendLine($"max output tokens: {settings.MaxOutputTokens}");
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
        AppendPromptDiagnostics(builder, request);
        builder.AppendLine($"evidence count: {request.Sources.Count}");
        builder.AppendLine($"think:false を送ったか: {(request.Settings.DisableThinking ? "yes" : "no")}");
        builder.AppendLine($"content returned: no");
        builder.AppendLine($"thinking returned: {(ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase) ? "yes" : "unknown")}");
        builder.AppendLine($"done_reason: (取得不可)");
        builder.AppendLine($"error: {FormatExceptionForUi(ex)}");
        return builder.ToString();
    }

    private static string FormatGenerationSuccessDiagnostics(AnswerDraftRequest request, AnswerDraftResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("回答生成診断");
        builder.AppendLine($"使用モデル: {ValueOrUnset(request.Settings.LlmProvider.ChatModel)}");
        builder.AppendLine($"timeout seconds: {request.Settings.LlmProvider.TimeoutSeconds}");
        builder.AppendLine($"max output tokens: {request.Settings.LlmProvider.MaxOutputTokens}");
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
        builder.AppendLine($"Cases: {result.IndexedCaseCount}");
        builder.AppendLine($"Notes/chunks: {result.IndexedNoteCount}");
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
        builder.AppendLine($"取り込み対象候補(.txt/.md): {result.SupportedFileCount}");
        builder.AppendLine($"取り込み済み: {result.IndexedFileCount}");
        builder.AppendLine($"内容判定で除外: {result.ContentExcludedFileCount}");
        builder.AppendLine($"空ファイルスキップ: {result.EmptyFileSkippedCount}");
        builder.AppendLine($"未対応ドキュメント形式: {result.UnsupportedDocumentFileCount}");
        builder.AppendLine($"対象外バイナリ/アーカイブ: {result.OutOfScopeFileCount}");
        builder.AppendLine($"その他未対応: {result.OtherUnsupportedFileCount}");
        builder.AppendLine($"未対応ファイル総数: {result.UnsupportedFileCount}");
        builder.AppendLine($"読み取り失敗: {result.ReadFailureCount}");
        builder.AppendLine($"重複ファイルスキップ: {result.DuplicateFileSkippedCount}");
        builder.AppendLine("PDF/DOCX/XLSX/PNGは現在未対応です。将来の文書抽出対応で取り込み予定です。");
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

        builder.AppendLine(selection.WasLimited
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
            return "No selected evidence was passed to the LLM.";
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

    private void CopyText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "クリップボードにコピーしました。";
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
