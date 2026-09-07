using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Core.Codex;
using WpfApplication = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed partial class CodexChatViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly string[] RagLabInternalMarkers =
    [
        "[RAG Evidence]",
        "[End RAG Evidence]",
        "Selection reason:",
        "Product match:",
        "Version match:",
        "Evidence 1",
    ];

    private readonly ICodexAppServerClient client;
    private readonly ICodexCaseFileScanner fileScanner;
    private readonly ICodexPromptComposer promptComposer;
    private readonly ICodexSessionStore sessionStore;
    private readonly ICodexTechnicalValueDiffDetector diffDetector;
    private readonly ICodexEvidenceAbComparisonService abComparisonService;
    private readonly ICodexAttachmentContentReader attachmentContentReader;
    private readonly IRagLabEvidenceLoader ragLabEvidenceLoader;
    private readonly ICodexDiagnosticLogger logger;
    private readonly Func<CodexCaseSnapshot> caseProvider;
    private readonly Func<string> executablePathProvider;
    private readonly Func<string, bool> applyReply;
    private readonly Func<string, bool> applyMemo;
    private readonly Func<string, Task<bool>>? sendToWpfNoteEditor;
    private readonly Func<(string? Model, string? ReasoningEffort)>? codexSelectionProvider;
    private readonly Action<string?, string?>? codexSelectionUpdated;
    private readonly Func<(string? Model, string? ReasoningEffort)>? caseCodexSelectionProvider;
    private readonly Action<string?, string?>? caseCodexSelectionUpdated;
    private readonly Action<bool> undoApplication;
    private readonly Func<bool, bool> canUndoApplication;
    private readonly IExcelTranslationService excelTranslationService;
    private readonly IArtifactPromptComposer artifactPromptComposer;
    private readonly ArtifactRequestDetector artifactRequestDetector;
    private readonly ExcelTranslationJsonParser translationJsonParser;
    private readonly CaseArtifactPathPolicy artifactPathPolicy;
    private CodexConnectionState connectionState = CodexConnectionState.Disconnected;
    private string connectionDetails = "Codexへ接続してください。";
    private string version = "-";
    private string model = "Codex側の既定モデル";
    private string selectedModel = string.Empty;
    private string selectedReasoningEffort = string.Empty;
    private string caseSelectedModel = string.Empty;
    private string caseSelectedReasoningEffort = string.Empty;
    private string newThreadModel = string.Empty;
    private string newThreadReasoningEffort = string.Empty;
    private bool caseSelectionUpdating;
    private string actualModel = "-";
    private string actualReasoningEffort = "-";
    private string accountStatus = "未確認";
    private string threadId = "-";
    private string promptInput = string.Empty;
    private CodexPromptPreset? selectedPreset;
    private string warningText = string.Empty;
    private string errorText = string.Empty;
    private string technicalAnswer = string.Empty;
    private string reviewAnswer = string.Empty;
    private string reviewChanges = string.Empty;
    private string reviewWarnings = string.Empty;
    private string fileScanStatus = "未読込";
    private string caseFolderSendStatus = "案件フォルダを確認しています。";
    private string previousSessionStatus = "未確認";
    private bool hasPreviousSession;
    private bool caseFolderReady;
    private bool turnActive;
    private bool isReviewTurn;
    private string reviewBaseline = string.Empty;
    private CodexChatMessageViewModel? currentAssistantMessage;
    private CodexSession? currentSession;
    private CodexCaseSnapshot? currentSnapshot;
    private string scannedCaseFolder = string.Empty;
    private readonly HashSet<string> confirmedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private bool hasSentInitialContext;
    private long? activeTurnStartedTimestamp;
    private string activeComparisonKey = string.Empty;
    private IReadOnlyList<string> activeExistingEvidenceSourceTypes = [];
    private IReadOnlyList<RagLabEvidenceItem> activeRagLabEvidence = [];
    private CodexAbAnswerSample? latestCompletedAbSample;
    private CodexAbAnswerSample? baselineAbSample;
    private CodexAbAnswerSample? evidenceAbSample;
    private string baselineAbStatus = "未記録";
    private string evidenceAbStatus = "未記録";
    private string abComparisonText = "A/B回答を記録すると比較できます。";

    public CodexChatViewModel(
        ICodexAppServerClient client,
        ICodexCaseFileScanner fileScanner,
        ICodexPromptComposer promptComposer,
        ICodexSessionStore sessionStore,
        ICodexTechnicalValueDiffDetector diffDetector,
        ICodexDiagnosticLogger logger,
        Func<CodexCaseSnapshot> caseProvider,
        Func<string> executablePathProvider,
        Func<string, bool> applyReply,
        Func<string, bool> applyMemo,
        Action<bool> undoApplication,
        ICodexAttachmentContentReader? attachmentContentReader = null,
        IExcelTranslationService? excelTranslationService = null,
        IArtifactPromptComposer? artifactPromptComposer = null,
        ArtifactRequestDetector? artifactRequestDetector = null,
        ExcelTranslationJsonParser? translationJsonParser = null,
        CaseArtifactPathPolicy? artifactPathPolicy = null,
        IRagLabEvidenceLoader? ragLabEvidenceLoader = null,
        ICodexEvidenceAbComparisonService? abComparisonService = null,
        Func<bool, bool>? canUndoApplication = null,
        Func<string, Task<bool>>? sendToWpfNoteEditor = null,
        Func<(string? Model, string? ReasoningEffort)>? codexSelectionProvider = null,
        Action<string?, string?>? codexSelectionUpdated = null,
        Func<(string? Model, string? ReasoningEffort)>? caseCodexSelectionProvider = null,
        Action<string?, string?>? caseCodexSelectionUpdated = null)
    {
        this.client = client;
        this.fileScanner = fileScanner;
        this.promptComposer = promptComposer;
        this.sessionStore = sessionStore;
        this.diffDetector = diffDetector;
        this.abComparisonService = abComparisonService ?? new CodexEvidenceAbComparisonService(diffDetector);
        this.attachmentContentReader = attachmentContentReader ?? new CodexAttachmentContentReader();
        this.ragLabEvidenceLoader = ragLabEvidenceLoader ?? new RagLabEvidenceLoader();
        this.logger = logger;
        this.caseProvider = caseProvider;
        this.executablePathProvider = executablePathProvider;
        this.applyReply = applyReply;
        this.applyMemo = applyMemo;
        this.sendToWpfNoteEditor = sendToWpfNoteEditor;
        this.undoApplication = undoApplication;
        this.canUndoApplication = canUndoApplication ?? (_ => false);
        this.codexSelectionProvider = codexSelectionProvider;
        this.codexSelectionUpdated = codexSelectionUpdated;
        this.caseCodexSelectionProvider = caseCodexSelectionProvider;
        this.caseCodexSelectionUpdated = caseCodexSelectionUpdated;
        var savedSelection = codexSelectionProvider?.Invoke() ?? (null, null);
        selectedModel = savedSelection.Model?.Trim() ?? string.Empty;
        selectedReasoningEffort = savedSelection.ReasoningEffort?.Trim() ?? string.Empty;
        this.excelTranslationService = excelTranslationService ?? new ExcelTranslationService();
        this.artifactPromptComposer = artifactPromptComposer ?? new ArtifactPromptComposer();
        this.artifactRequestDetector = artifactRequestDetector ?? new ArtifactRequestDetector();
        this.translationJsonParser = translationJsonParser ?? new ExcelTranslationJsonParser();
        this.artifactPathPolicy = artifactPathPolicy ?? new CaseArtifactPathPolicy();

        ConnectCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(ConnectAsync), () => !turnActive);
        ReconnectCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(ReconnectAsync), () => !turnActive);
        StartNewCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(StartNewAsync), CanStartThread);
        ResumeCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(ResumeAsync), () => CanStartThread() && hasPreviousSession);
        SendCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(SendAsync), CanSend);
        StopCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(StopAsync), () => turnActive);
        RefreshFilesCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(RefreshFilesAsync), () => !turnActive);
        ApplyReplyCommand = new RelayCommand(ApplyLatestReply, HasLatestAnswer);
        ApplyMemoCommand = new RelayCommand(ApplyLatestMemo, HasLatestAnswer);
        SendToWpfNoteCommand = new AsyncRelayCommand(SendLatestAnswerToWpfNoteAsync, CanSendToWpfNote);
        UndoReplyCommand = new RelayCommand(() =>
        {
            undoApplication(true);
            RaiseCommandStates();
        }, () => this.canUndoApplication(true));
        UndoMemoCommand = new RelayCommand(() =>
        {
            undoApplication(false);
            RaiseCommandStates();
        }, () => this.canUndoApplication(false));
        CopyAnswerCommand = new RelayCommand(CopyLatestAnswer, HasLatestAnswer);
        FinalReviewCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(FinalReviewAsync), CanFinalReview);
        CaptureAbBaselineCommand = new RelayCommand(CaptureAbBaseline, CanCaptureAbBaseline);
        CaptureAbEvidenceCommand = new RelayCommand(CaptureAbEvidence, CanCaptureAbEvidence);
        CompareAbCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(CompareAbAsync), CanCompareAb);
        InitializeArtifactCommands();

        client.StateChanged += OnStateChanged;
        client.AgentMessageDelta += OnAgentMessageDelta;
        client.TurnCompleted += OnTurnCompleted;
        client.ItemStarted += OnItemActivity;
        client.ItemCompleted += OnItemActivity;
        client.Warning += OnWarning;
        client.Error += OnError;
    }

    public ObservableCollection<CodexChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<CodexCaseFileViewModel> Files { get; } = [];
    public ObservableCollection<CodexModelInfo> AvailableModels { get; } = [];
    public ObservableCollection<CodexReasoningEffortInfo> AvailableReasoningEfforts { get; } = [];
    public ObservableCollection<CodexCaseModelOption> AvailableCaseModels { get; } = [];
    public ObservableCollection<CodexCaseReasoningEffortOption> AvailableCaseReasoningEfforts { get; } = [];
    public IReadOnlyList<CodexPromptPreset> PromptPresets { get; } = CodexPromptPreset.Defaults;

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public AsyncRelayCommand StartNewCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshFilesCommand { get; }
    public RelayCommand ApplyReplyCommand { get; }
    public AsyncRelayCommand SendToWpfNoteCommand { get; }
    public RelayCommand ApplyMemoCommand { get; }
    public RelayCommand UndoReplyCommand { get; }
    public RelayCommand UndoMemoCommand { get; }
    public RelayCommand CopyAnswerCommand { get; }
    public AsyncRelayCommand FinalReviewCommand { get; }
    public RelayCommand CaptureAbBaselineCommand { get; }
    public RelayCommand CaptureAbEvidenceCommand { get; }
    public AsyncRelayCommand CompareAbCommand { get; }

    public CodexConnectionState ConnectionState
    {
        get => connectionState;
        private set
        {
            if (SetProperty(ref connectionState, value))
            {
                OnPropertyChanged(nameof(ConnectionStateText));
            }
        }
    }

    public string ConnectionStateText => ConnectionState.ToJapanese();
    public int ProgressPercent => ConnectionState switch
    {
        CodexConnectionState.Disconnected => 0,
        CodexConnectionState.Connecting => 10,
        CodexConnectionState.Connected => 0,
        CodexConnectionState.StartingThread => 30,
        CodexConnectionState.Investigating => 45,
        CodexConnectionState.GeneratingAnswer => 75,
        CodexConnectionState.Interrupting => 85,
        CodexConnectionState.Completed => 100,
        CodexConnectionState.ReconnectRequired => 0,
        CodexConnectionState.AuthenticationRequired => 0,
        CodexConnectionState.Error => 0,
        _ => 0,
    };
    public string ProgressText => ConnectionState switch
    {
        CodexConnectionState.Disconnected => "未接続",
        CodexConnectionState.Connected => "接続済み・調査待ち",
        CodexConnectionState.ReconnectRequired => "再接続が必要",
        CodexConnectionState.AuthenticationRequired => "認証が必要",
        CodexConnectionState.Error => "エラー",
        _ => $"{ConnectionStateText} {ProgressPercent}%",
    };

    public string ConnectionDetails
    {
        get => connectionDetails;
        private set => SetProperty(ref connectionDetails, value);
    }

    public string Version
    {
        get => version;
        private set => SetProperty(ref version, value);
    }

    public string Model
    {
        get => model;
        private set => SetProperty(ref model, value);
    }

    public string SelectedModel
    {
        get => selectedModel;
        set => SetModelSelection(value, persist: true);
    }

    public string SelectedReasoningEffort
    {
        get => selectedReasoningEffort;
        set => SetReasoningSelection(value, persist: true);
    }

    public string CaseSelectedModel
    {
        get => caseSelectedModel;
        set => SetCaseModelSelection(value, persist: true);
    }

    public string CaseSelectedReasoningEffort
    {
        get => caseSelectedReasoningEffort;
        set => SetCaseReasoningSelection(value, persist: true);
    }

    public string GlobalCodexSelectionSummary =>
        $"{ValueOrDash(SelectedModel)} / {FormatReasoningDisplay(SelectedReasoningEffort)}";

    public string CaseCodexSelectionSummary =>
        $"{FormatCaseModelDisplay(CaseSelectedModel)} / {FormatCaseReasoningDisplay(CaseSelectedReasoningEffort)}";

    public string CaseCodexSelectionStatus => BuildCaseSelectionStatus();

    public string EffectiveCodexModel => EffectiveNewThreadModel();
    public string EffectiveCodexReasoning => EffectiveNewThreadReasoningEffort();
    public string GlobalSettingsDisplay => DisplaySettings(SelectedModel, SelectedReasoningEffort);
    public string CaseSettingsDisplay =>
        $"{(string.IsNullOrWhiteSpace(CaseSelectedModel) ? "全体設定を使用" : DisplayModel(CaseSelectedModel))} / {(string.IsNullOrWhiteSpace(CaseSelectedReasoningEffort) ? "全体設定を使用" : DisplayReasoning(CaseSelectedReasoningEffort))}";
    public string EffectiveSettingsDisplay => DisplaySettings(EffectiveCodexModel, EffectiveCodexReasoning);
    public string ActualThreadSettingsDisplay => IsActualThreadForCurrentCase
        ? DisplaySettings(ActualModel, ActualReasoningEffort) : "不明 / 不明";

    private bool IsActualThreadForCurrentCase
    {
        get
        {
            var snapshot = caseProvider();
            return currentSnapshot is not null
                && string.Equals(currentSnapshot.SupportId, snapshot.SupportId, StringComparison.OrdinalIgnoreCase)
                && currentSnapshot.ProductId == snapshot.ProductId
                && PathEquals(currentSnapshot.CaseFolder, snapshot.CaseFolder);
        }
    }

    public string CaseSettingsDiagnostics
    {
        get
        {
            var snapshot = caseProvider();
            var identity = System.Text.Json.JsonSerializer.Serialize(new
            {
                snapshot.SupportId, snapshot.ProductId,
                CaseFolder = snapshot.CaseFolder?.Trim().TrimEnd('\\', '/').ToUpperInvariant(),
            });
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
            return $"CaseCodexSettings:\nGlobalModel: {SelectedModel}\nGlobalReasoning: {SelectedReasoningEffort}\nCaseOverrideModel: {CaseSelectedModel}\nCaseOverrideReasoning: {CaseSelectedReasoningEffort}\nEffectiveModel: {EffectiveCodexModel}\nEffectiveReasoning: {EffectiveCodexReasoning}\nActualThreadModel: {(IsActualThreadForCurrentCase ? ActualModel : "-")}\nActualThreadReasoning: {(IsActualThreadForCurrentCase ? ActualReasoningEffort : "-")}\nCaseIdentity: SHA256:{hash}";
        }
    }

    public static string DisplayModel(string? value) => value switch
    {
        "gpt-6-astra" => "Astra", "gpt-5.6-sol" => "Sol",
        "gpt-5.6-terra" => "Terra", "gpt-5.6-luna" => "Luna",
        null or "" or "-" => "不明", _ => value,
    };

    public static string DisplayReasoning(string? value) => value switch
    {
        "low" => "低", "medium" => "中", "high" => "高", "xhigh" => "超高",
        "max" => "最大", "ultra" => "最上位", null or "" or "-" => "不明", _ => value,
    };

    private static string DisplaySettings(string? model, string? reasoning) =>
        $"{DisplayModel(model)} / {DisplayReasoning(reasoning)}";

    protected override void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName is nameof(SelectedModel) or nameof(SelectedReasoningEffort)
            or nameof(CaseSelectedModel) or nameof(CaseSelectedReasoningEffort)
            or nameof(ActualModel) or nameof(ActualReasoningEffort) or nameof(CaseCodexSelectionStatus))
        {
            foreach (var name in new[] { nameof(EffectiveCodexModel), nameof(EffectiveCodexReasoning),
                nameof(GlobalSettingsDisplay), nameof(CaseSettingsDisplay), nameof(EffectiveSettingsDisplay),
                nameof(ActualThreadSettingsDisplay), nameof(CaseSettingsDiagnostics) })
                base.OnPropertyChanged(name);
        }
    }

    public string ActualModel
    {
        get => actualModel;
        private set
        {
            if (SetProperty(ref actualModel, value))
            {
                OnPropertyChanged(nameof(ActualModelAndReasoning));
            }
        }
    }

    public string ActualReasoningEffort
    {
        get => actualReasoningEffort;
        private set
        {
            if (SetProperty(ref actualReasoningEffort, value))
            {
                OnPropertyChanged(nameof(ActualModelAndReasoning));
            }
        }
    }

    public string ActualModelAndReasoning => $"{ValueOrDash(ActualModel)} / {ValueOrDash(ActualReasoningEffort)}";

    public string CodexSelectionStatus => BuildSelectionStatus();

    public string AccountStatus
    {
        get => accountStatus;
        private set => SetProperty(ref accountStatus, value);
    }

    public string ThreadId
    {
        get => threadId;
        private set => SetProperty(ref threadId, value);
    }

    public string PromptInput
    {
        get => promptInput;
        set
        {
            if (SetProperty(ref promptInput, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public CodexPromptPreset? SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (SetProperty(ref selectedPreset, value) && value is not null)
            {
                PromptInput = value.Prompt;
            }
        }
    }

    public string WarningText
    {
        get => warningText;
        private set => SetProperty(ref warningText, value);
    }

    public string ErrorText
    {
        get => errorText;
        private set => SetProperty(ref errorText, value);
    }

    public string TechnicalAnswer
    {
        get => technicalAnswer;
        set
        {
            if (SetProperty(ref technicalAnswer, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ReviewAnswer
    {
        get => reviewAnswer;
        private set
        {
            if (SetProperty(ref reviewAnswer, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ReviewChanges
    {
        get => reviewChanges;
        private set => SetProperty(ref reviewChanges, value);
    }

    public string ReviewWarnings
    {
        get => reviewWarnings;
        private set => SetProperty(ref reviewWarnings, value);
    }

    public string BaselineAbStatus
    {
        get => baselineAbStatus;
        private set => SetProperty(ref baselineAbStatus, value);
    }

    public string EvidenceAbStatus
    {
        get => evidenceAbStatus;
        private set => SetProperty(ref evidenceAbStatus, value);
    }

    public string AbComparisonText
    {
        get => abComparisonText;
        private set => SetProperty(ref abComparisonText, value);
    }

    public string FileScanStatus
    {
        get => fileScanStatus;
        private set => SetProperty(ref fileScanStatus, value);
    }

    public string CaseFolderSendStatus
    {
        get => caseFolderSendStatus;
        private set => SetProperty(ref caseFolderSendStatus, value);
    }

    public string PreviousSessionStatus
    {
        get => previousSessionStatus;
        private set => SetProperty(ref previousSessionStatus, value);
    }

    public string DiagnosticsPath => logger.LogDirectory;
    public string SelectedFilesSummary => $"選択: {Files.Count(static file => file.IsSelected)} / 表示: {Files.Count}";

    public async Task InitializeAsync()
    {
        var savedSelection = codexSelectionProvider?.Invoke();
        RunOnUi(() =>
        {
            if (savedSelection is { } selection)
            {
                SetModelSelection(selection.Model, persist: false);
                SetReasoningSelection(selection.ReasoningEffort, persist: false);
            }
        });
        RefreshCaseSelection();
        await RefreshFilesAsync().ConfigureAwait(false);
        await FindPreviousSessionAsync().ConfigureAwait(false);
    }

    public void RefreshCaseSelection()
    {
        var savedSelection = caseCodexSelectionProvider?.Invoke() ?? (null, null);
        RunOnUi(() =>
        {
            SetCaseModelSelection(savedSelection.Model, persist: false);
            SetCaseReasoningSelection(savedSelection.ReasoningEffort, persist: false);
            RebuildCaseSelectionOptions();
        });
    }

    public async Task ShutdownAsync()
    {
        if (turnActive)
        {
            try
            {
                await client.InterruptTurnAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await PersistSessionAsync("closed").ConfigureAwait(false);
        await client.DisconnectAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        client.StateChanged -= OnStateChanged;
        client.AgentMessageDelta -= OnAgentMessageDelta;
        client.TurnCompleted -= OnTurnCompleted;
        client.ItemStarted -= OnItemActivity;
        client.ItemCompleted -= OnItemActivity;
        client.Warning -= OnWarning;
        client.Error -= OnError;
        foreach (var file in Files)
        {
            file.PropertyChanged -= OnCaseFilePropertyChanged;
        }

        await client.DisposeAsync().ConfigureAwait(false);
        if (logger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }
    }

    private void SetModelSelection(string? value, bool persist)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (persist && string.IsNullOrWhiteSpace(normalized)
            && !string.IsNullOrWhiteSpace(selectedModel)
            && AvailableModels.Count == 0)
        {
            return;
        }

        if (!string.Equals(selectedModel, normalized, StringComparison.Ordinal))
        {
            selectedModel = normalized;
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(GlobalCodexSelectionSummary));
            OnPropertyChanged(nameof(CaseCodexSelectionStatus));
        }

        UpdateReasoningEfforts();
        RebuildCaseReasoningEfforts();
        if (persist)
        {
            codexSelectionUpdated?.Invoke(SelectedModel, SelectedReasoningEffort);
        }
    }

    private void SetReasoningSelection(string? value, bool persist)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (persist && string.IsNullOrWhiteSpace(normalized)
            && !string.IsNullOrWhiteSpace(selectedReasoningEffort)
            && AvailableModels.Count == 0)
        {
            return;
        }

        if (!string.Equals(selectedReasoningEffort, normalized, StringComparison.Ordinal))
        {
            selectedReasoningEffort = normalized;
            OnPropertyChanged(nameof(SelectedReasoningEffort));
            OnPropertyChanged(nameof(GlobalCodexSelectionSummary));
            OnPropertyChanged(nameof(CaseCodexSelectionStatus));
        }

        OnPropertyChanged(nameof(CodexSelectionStatus));
        RebuildCaseReasoningEfforts();
        if (persist)
        {
            codexSelectionUpdated?.Invoke(SelectedModel, SelectedReasoningEffort);
        }
    }

    private void SetCaseModelSelection(string? value, bool persist)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (caseSelectionUpdating && string.IsNullOrWhiteSpace(normalized)
            && !string.IsNullOrWhiteSpace(caseSelectedModel))
        {
            return;
        }

        if (!string.Equals(caseSelectedModel, normalized, StringComparison.Ordinal))
        {
            caseSelectedModel = normalized;
            OnPropertyChanged(nameof(CaseSelectedModel));
            OnPropertyChanged(nameof(CaseCodexSelectionSummary));
        }

        RebuildCaseReasoningEfforts();
        OnPropertyChanged(nameof(CaseCodexSelectionStatus));
        if (persist)
        {
            caseCodexSelectionUpdated?.Invoke(
                NullIfWhiteSpace(CaseSelectedModel),
                NullIfWhiteSpace(CaseSelectedReasoningEffort));
        }
    }

    private void SetCaseReasoningSelection(string? value, bool persist)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (caseSelectionUpdating && string.IsNullOrWhiteSpace(normalized)
            && !string.IsNullOrWhiteSpace(caseSelectedReasoningEffort))
        {
            return;
        }

        if (!string.Equals(caseSelectedReasoningEffort, normalized, StringComparison.Ordinal))
        {
            caseSelectedReasoningEffort = normalized;
            OnPropertyChanged(nameof(CaseSelectedReasoningEffort));
            OnPropertyChanged(nameof(CaseCodexSelectionSummary));
        }

        OnPropertyChanged(nameof(CaseCodexSelectionStatus));
        if (persist)
        {
            caseCodexSelectionUpdated?.Invoke(
                NullIfWhiteSpace(CaseSelectedModel),
                NullIfWhiteSpace(CaseSelectedReasoningEffort));
        }
    }

    private void UpdateReasoningEfforts()
    {
        var selected = FindSelectedModel();
        AvailableReasoningEfforts.Clear();
        if (selected is not null)
        {
            foreach (var effort in EffectiveReasoningEfforts(selected))
            {
                AvailableReasoningEfforts.Add(effort);
            }
        }

        OnPropertyChanged(nameof(CodexSelectionStatus));
    }

    private void RebuildCaseSelectionOptions()
    {
        caseSelectionUpdating = true;
        try
        {
            AvailableCaseModels.Clear();
            AvailableCaseModels.Add(new CodexCaseModelOption(string.Empty, "全体設定を使用", IsInherit: true));
            foreach (var availableModel in AvailableModels)
            {
                if (AvailableCaseModels.Any(item => string.Equals(item.Id, availableModel.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                AvailableCaseModels.Add(new CodexCaseModelOption(availableModel.Id, availableModel.SelectionDisplayName));
            }

        }
        finally
        {
            caseSelectionUpdating = false;
        }

        RebuildCaseReasoningEfforts();
        OnPropertyChanged(nameof(CaseCodexSelectionSummary));
        OnPropertyChanged(nameof(CaseCodexSelectionStatus));
    }

    private void RebuildCaseReasoningEfforts()
    {
        caseSelectionUpdating = true;
        try
        {
            AvailableCaseReasoningEfforts.Clear();
            AvailableCaseReasoningEfforts.Add(new CodexCaseReasoningEffortOption(string.Empty, "全体設定を使用", IsInherit: true));

            var selectedModel = string.IsNullOrWhiteSpace(CaseSelectedModel)
                ? FindSelectedModel()
                : FindAvailableModel(CaseSelectedModel);
            if (selectedModel is not null)
            {
                foreach (var effort in EffectiveReasoningEfforts(selectedModel))
                {
                    AvailableCaseReasoningEfforts.Add(new CodexCaseReasoningEffortOption(
                        effort.Value,
                        CodexReasoningEffortDisplay.Format(effort.Value, effort.Description)));
                }
            }

            if (!string.IsNullOrWhiteSpace(CaseSelectedReasoningEffort)
                && !AvailableCaseReasoningEfforts.Any(item =>
                    string.Equals(item.Id, CaseSelectedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
            {
                AvailableCaseReasoningEfforts.Add(new CodexCaseReasoningEffortOption(
                    CaseSelectedReasoningEffort,
                    $"{CodexReasoningEffortDisplay.Format(CaseSelectedReasoningEffort)} (現在のruntimeでは未提供)"));
            }
        }
        finally
        {
            caseSelectionUpdating = false;
        }

        OnPropertyChanged(nameof(CaseCodexSelectionSummary));
        OnPropertyChanged(nameof(CaseCodexSelectionStatus));
    }

    private CodexModelInfo? FindSelectedModel()
    {
        return FindAvailableModel(SelectedModel);
    }

    private CodexModelInfo? FindAvailableModel(string? model)
    {
        return AvailableModels.FirstOrDefault(item =>
            string.Equals(item.Id, model, StringComparison.OrdinalIgnoreCase));
    }

    private CodexModelInfo ValidateNewThreadSelection()
    {
        var requestedModel = EffectiveNewThreadModel();
        var selected = FindAvailableModel(requestedModel);
        if (selected is null)
        {
            throw new InvalidOperationException(
                $"選択したCodexモデルは現在のApp Serverで利用できません。Requested Model: {ValueOrDash(requestedModel)}。接続後に利用可能なモデルを選択してください。");
        }

        var efforts = EffectiveReasoningEfforts(selected);
        var requestedReasoningEffort = EffectiveNewThreadReasoningEffort();
        if (efforts.Count == 0 && !string.IsNullOrWhiteSpace(requestedReasoningEffort))
        {
            throw new InvalidOperationException(
                $"選択した推論レベルはこのモデルでは広告されていません: {requestedReasoningEffort}。推論レベルを空欄にしてサーバー既定値を使用してください。");
        }

        if (efforts.Count > 0 && string.IsNullOrWhiteSpace(requestedReasoningEffort))
        {
            throw new InvalidOperationException(
                $"推論レベルが未選択です。モデル {selected.Id} の利用可能な値: {FormatReasoningEfforts(efforts)}");
        }

        if (efforts.Count > 0 && !efforts.Any(item =>
                string.Equals(item.Value, requestedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"選択した推論レベルはこのモデルでは利用できません: {requestedReasoningEffort}。利用可能な値: {FormatReasoningEfforts(efforts)}");
        }

        return selected;
    }

    private string EffectiveNewThreadModel() =>
        string.IsNullOrWhiteSpace(CaseSelectedModel) ? SelectedModel : CaseSelectedModel;

    private string EffectiveNewThreadReasoningEffort() =>
        string.IsNullOrWhiteSpace(CaseSelectedReasoningEffort)
            ? SelectedReasoningEffort
            : CaseSelectedReasoningEffort;

    private string BuildSelectionStatus()
    {
        if (AvailableModels.Count == 0)
        {
            return "Codexへ接続すると、利用可能なモデルと推論レベルを取得します。";
        }

        var selected = FindSelectedModel();
        if (selected is null)
        {
            return $"Requested Model: {ValueOrDash(SelectedModel)} は利用できません。利用可能: {string.Join(", ", AvailableModels.Select(static item => item.Id))}";
        }

        var efforts = EffectiveReasoningEfforts(selected);
        if (efforts.Count == 0 && !string.IsNullOrWhiteSpace(SelectedReasoningEffort))
        {
            return $"選択した推論レベルはこのモデルでは広告されていません: {SelectedReasoningEffort}。空欄にしてください。";
        }

        if (efforts.Count > 0 && string.IsNullOrWhiteSpace(SelectedReasoningEffort))
        {
            return $"推論レベルを選択してください。利用可能: {FormatReasoningEfforts(efforts)}";
        }

        if (efforts.Count == 0)
        {
            return string.IsNullOrWhiteSpace(client.CurrentThreadId)
                ? "このモデルは推論レベルを広告していません。サーバー既定値で次の新しい調査を開始します。"
                : "現在のThreadは変更せず、このモデルの推論レベルはサーバー既定値で次の新しい調査から適用されます。";
        }

        if (efforts.Count > 0 && !efforts.Any(item =>
                string.Equals(item.Value, SelectedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
        {
            return $"選択した推論レベルはこのモデルでは利用できません: {SelectedReasoningEffort}。利用可能: {FormatReasoningEfforts(efforts)}";
        }

        return string.IsNullOrWhiteSpace(client.CurrentThreadId)
            ? "この設定は次の新しい調査から適用されます。"
            : "現在のThreadは変更せず、この設定は次の新しい調査から適用されます。";
    }

    private string BuildCaseSelectionStatus()
    {
        var effectiveModel = EffectiveNewThreadModel();
        var effectiveReasoningEffort = EffectiveNewThreadReasoningEffort();
        var selected = FindAvailableModel(effectiveModel);
        var prefix = $"全体既定: {GlobalCodexSelectionSummary}{Environment.NewLine}"
            + $"案件設定: {CaseCodexSelectionSummary}{Environment.NewLine}";

        if (AvailableModels.Count == 0)
        {
            return prefix + "App Serverへ接続すると、案件設定の候補を確認できます。";
        }

        if (selected is null)
        {
            return prefix + $"新しい調査: Requested Model {ValueOrDash(effectiveModel)} はruntimeで利用できません。";
        }

        var efforts = EffectiveReasoningEfforts(selected);
        if (efforts.Count > 0 && string.IsNullOrWhiteSpace(effectiveReasoningEffort))
        {
            return prefix + $"新しい調査: 推論レベルを選択してください。利用可能: {FormatReasoningEfforts(efforts)}";
        }

        if (efforts.Count > 0 && !efforts.Any(item =>
                string.Equals(item.Value, effectiveReasoningEffort, StringComparison.OrdinalIgnoreCase)))
        {
            return prefix + $"新しい調査: 推論レベル {ValueOrDash(effectiveReasoningEffort)} はruntimeで利用できません。";
        }

        return prefix + $"新しい調査: {FormatCaseModelDisplay(effectiveModel)} / {FormatReasoningDisplay(effectiveReasoningEffort)}"
            + "（既存Threadは変更しません）";
    }

    private static IReadOnlyList<CodexReasoningEffortInfo> EffectiveReasoningEfforts(CodexModelInfo model)
    {
        if (model.ReasoningEfforts.Count > 0)
        {
            return model.ReasoningEfforts;
        }

        return string.IsNullOrWhiteSpace(model.DefaultReasoningEffort)
            ? []
            : [new CodexReasoningEffortInfo(model.DefaultReasoningEffort, "モデル既定")];
    }

    private static string? FindDefaultReasoningEffort(CodexModelInfo? model)
    {
        if (model is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(model.DefaultReasoningEffort)
            ? model.ReasoningEfforts.FirstOrDefault()?.Value
            : model.DefaultReasoningEffort;
    }

    private static CodexModelInfo? FindRecommendedModel(IEnumerable<CodexModelInfo> models)
    {
        var available = models.Where(static item => !item.Hidden).ToArray();
        return available.FirstOrDefault(item =>
                item.Id.Contains("luna", StringComparison.OrdinalIgnoreCase)
                || item.DisplayName.Contains("luna", StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(static item => item.IsDefault)
            ?? available.FirstOrDefault();
    }

    private static string FormatReasoningEfforts(IEnumerable<CodexReasoningEffortInfo> efforts)
    {
        var values = efforts.Select(static item => item.Value).Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return values.Length == 0 ? "App Serverから取得できません" : string.Join(", ", values);
    }

    private string FormatCaseModelDisplay(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "全体設定を使用";
        }

        return FindAvailableModel(model)?.SelectionDisplayName ?? model.Trim();
    }

    private static string FormatCaseReasoningDisplay(string? reasoningEffort)
    {
        return string.IsNullOrWhiteSpace(reasoningEffort)
            ? "全体設定を使用"
            : CodexReasoningEffortDisplay.Format(reasoningEffort);
    }

    private static string FormatReasoningDisplay(string? reasoningEffort)
    {
        return string.IsNullOrWhiteSpace(reasoningEffort)
            ? "-"
            : CodexReasoningEffortDisplay.Format(reasoningEffort);
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task ConnectAsync()
    {
        ErrorText = string.Empty;
        var info = await client.ConnectAsync(executablePathProvider()).ConfigureAwait(false);
        RunOnUi(() =>
        {
            Version = info.Version;
            AccountStatus = $"ChatGPT認証済み / プラン: {ValueOrDash(info.Account.PlanType)}";
            AvailableModels.Clear();
            foreach (var availableModel in info.Models.Where(static item => !item.Hidden))
            {
                AvailableModels.Add(availableModel);
            }

            var recommendedModel = FindRecommendedModel(AvailableModels);
            if (string.IsNullOrWhiteSpace(SelectedModel))
            {
                SetModelSelection(recommendedModel?.Id, persist: true);
            }
            else
            {
                UpdateReasoningEfforts();
            }

            if (string.IsNullOrWhiteSpace(SelectedReasoningEffort))
            {
                SetReasoningSelection(FindDefaultReasoningEffort(FindSelectedModel()), persist: true);
            }

            Model = string.IsNullOrWhiteSpace(SelectedModel)
                ? "Codex側のモデル一覧を取得できません"
                : SelectedModel;
            RebuildCaseSelectionOptions();
            ActualModel = "-";
            ActualReasoningEffort = "-";
            var selectionStatus = BuildSelectionStatus();
            ErrorText = selectionStatus.Contains("利用できません", StringComparison.Ordinal)
                || selectionStatus.Contains("広告されていません", StringComparison.Ordinal)
                ? selectionStatus
                : string.Empty;
            ConnectionDetails = string.IsNullOrWhiteSpace(ErrorText)
                ? $"App Server利用可 / {info.UserAgent} / モデル一覧: {AvailableModels.Count}件"
                : $"App Server利用可 / {info.UserAgent} / モデル一覧: {AvailableModels.Count}件 / {ErrorText}";
            OnPropertyChanged(nameof(CodexSelectionStatus));
        });
    }

    private async Task ReconnectAsync()
    {
        await client.DisconnectAsync().ConfigureAwait(false);
        await ConnectAsync().ConfigureAwait(false);
        await FindPreviousSessionAsync().ConfigureAwait(false);
    }

    private async Task StartNewAsync()
    {
        await EnsureConnectedAsync().ConfigureAwait(false);
        var snapshot = caseProvider();
        if (string.IsNullOrWhiteSpace(snapshot.CaseFolder) || !Directory.Exists(snapshot.CaseFolder))
        {
            throw new InvalidOperationException("案件フォルダが見つかりません。案件を読み直すか、案件フォルダを選択してください。");
        }

        var selected = ValidateNewThreadSelection();
        newThreadModel = selected.Id;
        newThreadReasoningEffort = EffectiveNewThreadReasoningEffort();
        var result = await client.StartThreadAsync(snapshot.CaseFolder, selected.Id).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Model)
            && !string.Equals(result.Model, selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            RunOnUi(() =>
            {
                ActualModel = result.Model;
                ActualReasoningEffort = string.IsNullOrWhiteSpace(result.ReasoningEffort)
                    ? newThreadReasoningEffort
                    : result.ReasoningEffort;
            });
            throw new InvalidOperationException(
                $"Requested Model: {selected.Id}{Environment.NewLine}Actual Model: {result.Model}{Environment.NewLine}モデルが要求値と異なるため、回答生成を継続しません。");
        }

        currentSnapshot = snapshot;
        currentSession = CreateSession(snapshot, result, newThreadReasoningEffort);
        RunOnUi(() =>
        {
            Messages.Clear();
            hasSentInitialContext = false;
            latestCompletedAbSample = null;
            TechnicalAnswer = string.Empty;
            ReviewAnswer = string.Empty;
            activeComparisonKey = string.Empty;
            activeExistingEvidenceSourceTypes = [];
            activeRagLabEvidence = [];
            ThreadId = result.ThreadId;
            Model = string.IsNullOrWhiteSpace(result.Model) ? Model : result.Model;
            ActualModel = string.IsNullOrWhiteSpace(result.Model) ? selected.Id : result.Model;
            ActualReasoningEffort = string.IsNullOrWhiteSpace(result.ReasoningEffort)
                ? newThreadReasoningEffort
                : result.ReasoningEffort;
            ConnectionDetails = "新しい読み取り専用Threadを開始しました。指示を送信できます。";
            OnPropertyChanged(nameof(CodexSelectionStatus));
        });
        await PersistSessionAsync("ready").ConfigureAwait(false);
    }

    private async Task ResumeAsync()
    {
        await EnsureConnectedAsync().ConfigureAwait(false);
        var snapshot = caseProvider();
        var previous = await sessionStore.FindAsync(snapshot.SupportId, snapshot.ProductId, snapshot.CaseFolder).ConfigureAwait(false);
        if (previous is null || string.IsNullOrWhiteSpace(previous.CodexThreadId))
        {
            throw new InvalidOperationException("再開できるCodex Threadがありません。新しい調査を開始してください。");
        }

        try
        {
            var result = await client.ResumeThreadAsync(previous.CodexThreadId, snapshot.CaseFolder, model: null).ConfigureAwait(false);
            currentSnapshot = snapshot;
            currentSession = previous with
            {
                ProductId = snapshot.ProductId ?? previous.ProductId,
                CompanyName = string.IsNullOrWhiteSpace(snapshot.CompanyName) ? previous.CompanyName : snapshot.CompanyName,
                CaseFolder = snapshot.CaseFolder,
                LastUsedAt = DateTimeOffset.Now,
                SessionStatus = "resumed",
            };
            RunOnUi(() =>
            {
                Messages.Clear();
                latestCompletedAbSample = null;
                activeComparisonKey = string.Empty;
                activeExistingEvidenceSourceTypes = [];
                activeRagLabEvidence = [];
                foreach (var message in previous.Messages)
                {
                    Messages.Add(new CodexChatMessageViewModel
                    {
                        Role = message.Role,
                        Text = message.Text,
                        CreatedAt = message.CreatedAt,
                    });
                }

                TechnicalAnswer = Messages.LastOrDefault(static item => item.Role == "assistant")?.Text ?? string.Empty;
                hasSentInitialContext = true;
                ThreadId = result.ThreadId;
                Model = string.IsNullOrWhiteSpace(result.Model) ? previous.Model : result.Model;
                ActualModel = string.IsNullOrWhiteSpace(result.Model) ? previous.Model : result.Model;
                ActualReasoningEffort = string.IsNullOrWhiteSpace(result.ReasoningEffort)
                    ? ValueOrDash(previous.ReasoningEffort)
                    : result.ReasoningEffort;
                ConnectionDetails = "前回のCodex Threadを再開しました。追加質問を送信できます。";
                OnPropertyChanged(nameof(CodexSelectionStatus));
            });
            await PersistSessionAsync("resumed").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PreviousSessionStatus = "Thread再開に失敗しました。履歴は保持しています。新規Threadを自動作成していません。";
            throw new InvalidOperationException("前回のCodex Threadを再開できませんでした。再接続後に再試行するか、明示的に「新しい調査」を押してください。", ex);
        }
    }

    private async Task SendAsync()
    {
        var instruction = PromptInput.Trim();
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return;
        }

        if (artifactRequestDetector.IsExplicitExcelTranslationRequest(instruction))
        {
            await PrepareArtifactPlanAsync(instruction).ConfigureAwait(false);
            instruction += Environment.NewLine
                + "ファイルへの書込みはWPF成果物機能がユーザー確認後に行います。"
                + "読み取り専用を理由に拒否せず、調査上の注意点とメーカー確認論点を回答してください。";
        }

        if (string.IsNullOrWhiteSpace(client.CurrentThreadId))
        {
            await StartNewAsync().ConfigureAwait(false);
        }

        currentSnapshot = caseProvider();
        if (!string.Equals(scannedCaseFolder, currentSnapshot.CaseFolder, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshFilesAsync().ConfigureAwait(false);
        }

        var firstTurn = !hasSentInitialContext;
        var requestedModel = firstTurn
            ? (string.IsNullOrWhiteSpace(newThreadModel) ? EffectiveNewThreadModel() : newThreadModel)
            : string.Empty;
        var requestedReasoningEffort = firstTurn
            ? (string.IsNullOrWhiteSpace(newThreadModel)
                ? EffectiveNewThreadReasoningEffort()
                : newThreadReasoningEffort)
            : string.Empty;
        var selectedFiles = Files.Where(static file => file.IsSelected && file.CanSendToCodex).ToArray();
        RunOnUi(() => ConnectionDetails = "選択した添付ファイルを読み取り、文字コード変換と本文抽出を行っています。");
        var attachmentRead = await attachmentContentReader.ReadAsync(
            currentSnapshot.CaseFolder,
            selectedFiles.Select(static file => file.File).ToArray()).ConfigureAwait(false);
        string prompt;
        IReadOnlyList<string> compositionWarnings = [];
        if (firstTurn)
        {
            var ragLabEvidence = await LoadRagLabEvidenceSafelyAsync(currentSnapshot).ConfigureAwait(false);
            activeComparisonKey = CodexEvidenceAbComparisonService.CreateComparisonKey(
                currentSnapshot.ProductName,
                currentSnapshot.InquiryText);
            activeExistingEvidenceSourceTypes = currentSnapshot.Evidence
                .Select(static source => source.SourceType)
                .Where(static sourceType => !string.IsNullOrWhiteSpace(sourceType))
                .ToArray();
            activeRagLabEvidence = ragLabEvidence.Evidence.ToArray();
            var composition = promptComposer.ComposeInitialPrompt(new CodexInitialPromptContext
            {
                ProductId = currentSnapshot.ProductId,
                ProductName = currentSnapshot.ProductName,
                ProductPromptFilePath = currentSnapshot.ProductPromptFilePath,
                SupportToolSettingsFilePath = currentSnapshot.SupportToolSettingsFilePath,
                SupportId = currentSnapshot.SupportId,
                CompanyName = currentSnapshot.CompanyName,
                CustomerName = currentSnapshot.CustomerName,
                Status = currentSnapshot.Status,
                ReceptionDate = currentSnapshot.ReceptionDate,
                CaseFolder = currentSnapshot.CaseFolder,
                InquiryText = currentSnapshot.InquiryText,
                Attachments = selectedFiles
                    .Select(static file => new CodexPromptAttachment(file.RelativePath, file.File.Kind, file.File.Size))
                    .ToArray(),
                AttachmentContents = attachmentRead.Contents,
                Evidence = currentSnapshot.Evidence,
                RagLabEvidence = ragLabEvidence.Evidence,
                UserInstruction = instruction,
            });
            prompt = composition.Prompt;
            compositionWarnings = composition.Warnings.Concat(ragLabEvidence.Warnings).Distinct().ToArray();
        }
        else
        {
            prompt = promptComposer.ComposeFollowUpPrompt(instruction, attachmentRead.Contents);
        }

        var preparationWarnings = attachmentRead.Warnings.Concat(compositionWarnings).Distinct().ToArray();
        RunOnUi(() =>
        {
            foreach (var file in selectedFiles)
            {
                file.ConfirmationStatus = file.IsImageInput
                    ? "画像入力として送信"
                    : attachmentRead.Contents.Any(content => string.Equals(content.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
                        ? "本文読取済み (UTF-8正規化)"
                        : "一覧のみ送信";
            }
            if (preparationWarnings.Length > 0)
            {
                WarningText = string.Join(Environment.NewLine, preparationWarnings);
            }
        });

        var imagePaths = selectedFiles.Where(static file => file.IsImageInput).Select(static file => file.FullPath).ToArray();
        var userMessage = new CodexChatMessageViewModel { Role = "user", Text = instruction, CreatedAt = DateTimeOffset.Now };
        var assistantMessage = new CodexChatMessageViewModel
        {
            Role = "assistant",
            Text = string.Empty,
            CreatedAt = DateTimeOffset.Now,
            IsStreaming = true,
        };
        RunOnUi(() =>
        {
            Messages.Add(userMessage);
            Messages.Add(assistantMessage);
            currentAssistantMessage = assistantMessage;
            latestCompletedAbSample = null;
            PromptInput = string.Empty;
            turnActive = true;
            confirmedFiles.Clear();
            foreach (var file in Files.Where(static file => file.IsSelected))
            {
                if (file.ConfirmationStatus == "未確認")
                {
                    file.ConfirmationStatus = "確認待ち";
                }
            }

            RaiseCommandStates();
        });

        activeTurnStartedTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var turn = await client.StartTurnAsync(
                prompt,
                imagePaths,
                model: firstTurn ? requestedModel : null,
                reasoningEffort: firstTurn ? requestedReasoningEffort : null).ConfigureAwait(false);
            if (firstTurn
                && !string.IsNullOrWhiteSpace(turn.Model)
                && !string.Equals(turn.Model, requestedModel, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Requested Model: {requestedModel}{Environment.NewLine}Actual Model: {turn.Model}{Environment.NewLine}モデルが要求値と異なるため、回答生成を継続しません。");
            }

            if (firstTurn
                && !string.IsNullOrWhiteSpace(turn.ReasoningEffort)
                && !string.Equals(turn.ReasoningEffort, requestedReasoningEffort, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Requested Reasoning: {requestedReasoningEffort}{Environment.NewLine}Actual Reasoning: {turn.ReasoningEffort}{Environment.NewLine}推論レベルが要求値と異なるため、回答生成を継続しません。");
            }

            if (firstTurn)
            {
                RunOnUi(() =>
                {
                    ActualModel = string.IsNullOrWhiteSpace(turn.Model) ? requestedModel : turn.Model;
                    ActualReasoningEffort = string.IsNullOrWhiteSpace(turn.ReasoningEffort)
                        ? requestedReasoningEffort
                        : turn.ReasoningEffort;
                });
            }
            hasSentInitialContext = true;
            if (currentSession is not null && turnActive)
            {
                currentSession = currentSession with { LastTurnId = turn.TurnId, LastUsedAt = DateTimeOffset.Now, SessionStatus = "running" };
                await PersistSessionAsync("running").ConfigureAwait(false);
            }
        }
        catch
        {
            activeTurnStartedTimestamp = null;
            RunOnUi(() =>
            {
                turnActive = false;
                assistantMessage.IsStreaming = false;
                RaiseCommandStates();
            });
            throw;
        }
    }

    private async Task<RagLabEvidenceLoadResult> LoadRagLabEvidenceSafelyAsync(CodexCaseSnapshot snapshot)
    {
        if (!snapshot.UseRagLabEvidence)
        {
            return new RagLabEvidenceLoadResult
            {
                IsEnabled = false,
                FallbackReason = "Disabled",
            };
        }

        try
        {
            return await ragLabEvidenceLoader.LoadAsync(new RagLabEvidenceLoadRequest
            {
                IsEnabled = true,
                EvidenceFilePath = snapshot.RagLabEvidenceFilePath,
                BaselineReadinessFilePath = snapshot.RagLabBaselineReadinessFilePath,
                MaxItems = snapshot.RagLabEvidenceMaxItems,
                ExpectedProduct = snapshot.ProductName,
                ExpectedVersion = snapshot.TargetVersion,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await logger.WriteAsync(
                "rag-lab-evidence",
                $"RAG Lab Evidence loading failed; continuing without additional evidence. {ex.GetType().Name}",
                ex).ConfigureAwait(false);
            return new RagLabEvidenceLoadResult
            {
                IsEnabled = true,
                Warnings = [$"RAG Lab Evidenceの追加に失敗したため、従来経路で続行します: {ex.GetType().Name}"],
                FallbackReason = "UnexpectedLoadFailure",
            };
        }
    }

    private async Task StopAsync()
    {
        await client.InterruptTurnAsync().ConfigureAwait(false);
        RunOnUi(() => ConnectionDetails = "中止要求を送信しました。Turnの終了通知を待っています。");
    }

    private async Task RefreshFilesAsync()
    {
        var snapshot = caseProvider();
        var caseFolderChanged = !string.Equals(scannedCaseFolder, snapshot.CaseFolder, StringComparison.OrdinalIgnoreCase);
        var folderReady = !string.IsNullOrWhiteSpace(snapshot.CaseFolder)
            && Directory.Exists(snapshot.CaseFolder);
        var result = await fileScanner.ScanAsync(snapshot.CaseFolder).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (caseFolderChanged)
            {
                ResetArtifactForCaseChange();
            }

            foreach (var existing in Files)
            {
                existing.PropertyChanged -= OnCaseFilePropertyChanged;
            }

            Files.Clear();
            foreach (var file in result.Files)
            {
                var selectByDefault = file.CanSendToCodex;
                var item = new CodexCaseFileViewModel(file, selectByDefault);
                item.PropertyChanged += OnCaseFilePropertyChanged;
                Files.Add(item);
            }

            scannedCaseFolder = snapshot.CaseFolder;
            FileScanStatus = result.Warnings.Count == 0
                ? $"案件ファイルを読み込みました: {Files.Count}件"
                : $"案件ファイル: {Files.Count}件 / 警告: {result.Warnings.Count}件";
            caseFolderReady = folderReady;
            CaseFolderSendStatus = folderReady
                ? string.Empty
                : "送信できません: 案件フォルダが見つかりません。案件を読み直すか、案件フォルダを選択してください。";
            WarningText = result.Warnings.Count == 0 ? WarningText : string.Join(Environment.NewLine, result.Warnings);
            OnPropertyChanged(nameof(SelectedFilesSummary));
            RaiseCommandStates();
        });
    }

    private void OnCaseFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodexCaseFileViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedFilesSummary));
            RaiseCommandStates();
        }
    }

    private async Task FinalReviewAsync()
    {
        if (string.IsNullOrWhiteSpace(client.CurrentThreadId))
        {
            throw new InvalidOperationException("最終レビューには同じ案件のCodex Threadが必要です。先に調査を開始してください。");
        }

        var snapshot = caseProvider();
        reviewBaseline = !string.IsNullOrWhiteSpace(snapshot.CustomerReplyDraft)
            && !string.Equals(snapshot.CustomerReplyDraft.Trim(), "まだ生成されていません。", StringComparison.Ordinal)
            ? snapshot.CustomerReplyDraft
            : TechnicalAnswer;
        isReviewTurn = true;
        ReviewAnswer = string.Empty;
        ReviewChanges = string.Empty;
        ReviewWarnings = string.Empty;
        PromptInput = BuildFinalReviewPrompt(snapshot, reviewBaseline);
        await SendAsync().ConfigureAwait(false);
    }

    private static string BuildFinalReviewPrompt(CodexCaseSnapshot snapshot, string draft)
    {
        return $"""
            以下のお客様向け回答案を最終レビューしてください。

            ## お客様の問い合わせ
            {snapshot.InquiryText}

            ## 調査結果・技術回答案
            {draft}

            ## 確定済みFact・使用根拠
            {string.Join(Environment.NewLine, snapshot.Evidence.Select(static source => $"- [{source.SourceType}] {source.Title}: {source.Text}"))}

            ## レビュー条件
            - 技術内容の整合性と問い合わせへの回答漏れを確認する。
            - バージョン、HF、EP、コマンド、設定値、エラーコード、パス、URL、製品名を勝手に変更しない。
            - 根拠がない技術情報を追加しない。不確かな点は要確認事項へ分離する。
            - 日本語表現、敬語、段落、重複を整え、既存のメール形式を維持する。
            - レビュー後の回答案、変更点、要確認事項、警告を明確に分ける。
            """;
    }

    private void OnAgentMessageDelta(object? sender, CodexAgentMessageDeltaEventArgs eventArgs)
    {
        RunOnUi(() =>
        {
            currentAssistantMessage ??= new CodexChatMessageViewModel
            {
                Role = "assistant",
                CreatedAt = DateTimeOffset.Now,
                IsStreaming = true,
            };
            if (!Messages.Contains(currentAssistantMessage))
            {
                Messages.Add(currentAssistantMessage);
            }

            currentAssistantMessage.Text += eventArgs.Delta;
            ConnectionDetails = "Codexから回答を受信しています。";
        });
    }

    private void OnTurnCompleted(object? sender, CodexTurnCompletedEventArgs eventArgs)
    {
        var artifactCompletion = artifactTurnCompletion;
        var artifactResponse = string.Empty;
        var generationDuration = activeTurnStartedTimestamp.HasValue
            ? Stopwatch.GetElapsedTime(activeTurnStartedTimestamp.Value)
            : TimeSpan.Zero;
        activeTurnStartedTimestamp = null;
        if (currentSession is not null)
        {
            currentSession = currentSession with { LastTurnId = eventArgs.TurnId };
        }

        RunOnUi(() =>
        {
            turnActive = false;
            if (currentAssistantMessage is not null)
            {
                currentAssistantMessage.IsStreaming = false;
                if (artifactCompletion is not null)
                {
                    artifactResponse = currentAssistantMessage.Text;
                }
                else if (isReviewTurn)
                {
                    ReviewAnswer = currentAssistantMessage.Text;
                    var diff = diffDetector.Compare(reviewBaseline, ReviewAnswer, [currentSnapshot?.ProductName ?? string.Empty]);
                    ReviewChanges = BuildDifferenceText(diff);
                    ReviewWarnings = diff.HasDifferences
                        ? "技術値の追加または削除を検出しました。自動採用せず、変更点を確認してください。"
                        : "技術値の差分は検出されませんでした。文章内容は手動で確認してください。";
                }
                else
                {
                    TechnicalAnswer = currentAssistantMessage.Text;
                }

                if (artifactCompletion is null
                    && eventArgs.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(activeComparisonKey))
                {
                    latestCompletedAbSample = new CodexAbAnswerSample
                    {
                        ComparisonKey = activeComparisonKey,
                        AnswerText = currentAssistantMessage.Text,
                        GenerationDuration = generationDuration,
                        ExistingEvidenceSourceTypes = activeExistingEvidenceSourceTypes.ToArray(),
                        RagLabEvidence = activeRagLabEvidence.ToArray(),
                    };
                }
            }

            isReviewTurn = false;
            currentAssistantMessage = null;
            foreach (var file in Files.Where(static file => file.IsSelected))
            {
                if (!confirmedFiles.Contains(file.RelativePath)
                    && file.ConfirmationStatus == "確認待ち")
                {
                    file.ConfirmationStatus = "確認イベントを検出できませんでした";
                }
            }

            ConnectionDetails = eventArgs.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                ? "CodexのTurnが完了しました。回答を確認してから返信案へ反映してください。"
                : $"CodexのTurnが終了しました: {eventArgs.Status} {eventArgs.ErrorMessage}";
            RaiseCommandStates();
        });
        if (artifactCompletion is not null)
        {
            if (eventArgs.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                artifactCompletion.TrySetResult(artifactResponse);
            }
            else
            {
                artifactCompletion.TrySetException(
                    new InvalidOperationException($"Codex成果物Turnが完了しませんでした: {eventArgs.Status} {eventArgs.ErrorMessage}"));
            }
        }
        _ = PersistSessionAsync(eventArgs.Status);
    }

    private void OnItemActivity(object? sender, CodexItemEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Path) || string.IsNullOrWhiteSpace(scannedCaseFolder))
        {
            return;
        }

        if (!CodexPathPolicy.TryNormalizeFileWithinRoot(scannedCaseFolder, eventArgs.Path, out var path, out _))
        {
            return;
        }

        var relative = Path.GetRelativePath(scannedCaseFolder, path);
        confirmedFiles.Add(relative);
        RunOnUi(() =>
        {
            var file = Files.FirstOrDefault(item => string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
            if (file is not null)
            {
                file.ConfirmationStatus = "Codex確認イベントあり";
            }
        });
    }

    private void OnStateChanged(object? sender, CodexConnectionState newState)
    {
        RunOnUi(() =>
        {
            ConnectionState = newState;
            if (newState is CodexConnectionState.ReconnectRequired or CodexConnectionState.Error)
            {
                turnActive = false;
            }
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            RaiseCommandStates();
        });
    }

    private void OnWarning(object? sender, string warning)
    {
        RunOnUi(() => WarningText = warning);
    }

    private void OnError(object? sender, string error)
    {
        activeTurnStartedTimestamp = null;
        artifactTurnCompletion?.TrySetException(new InvalidOperationException(error));
        RunOnUi(() =>
        {
            ErrorText = $"{error}{Environment.NewLine}現在の状態: {ConnectionStateText}{Environment.NewLine}再接続または再試行してください。{Environment.NewLine}診断ログ: {DiagnosticsPath}";
            turnActive = false;
            RaiseCommandStates();
        });
    }

    private async Task FindPreviousSessionAsync()
    {
        var snapshot = caseProvider();
        var load = await sessionStore.LoadAsync().ConfigureAwait(false);
        var previous = load.Sessions
            .Where(item => string.Equals(item.SupportId, snapshot.SupportId, StringComparison.OrdinalIgnoreCase))
            .Where(item => !snapshot.ProductId.HasValue || !item.ProductId.HasValue || item.ProductId == snapshot.ProductId)
            .OrderByDescending(item => PathEquals(item.CaseFolder, snapshot.CaseFolder))
            .ThenByDescending(static item => item.LastUsedAt)
            .FirstOrDefault();
        currentSnapshot = snapshot;
        currentSession = previous is null
            ? null
            : previous with
            {
                ProductId = snapshot.ProductId ?? previous.ProductId,
                CompanyName = string.IsNullOrWhiteSpace(snapshot.CompanyName) ? previous.CompanyName : snapshot.CompanyName,
                CaseFolder = snapshot.CaseFolder,
            };
        RunOnUi(() =>
        {
            latestCompletedAbSample = null;
            activeComparisonKey = string.Empty;
            activeExistingEvidenceSourceTypes = [];
            activeRagLabEvidence = [];
            hasPreviousSession = previous is not null && !string.IsNullOrWhiteSpace(previous.CodexThreadId);
            if (hasPreviousSession)
            {
                Messages.Clear();
                foreach (var message in previous!.Messages)
                {
                    Messages.Add(new CodexChatMessageViewModel
                    {
                        Role = message.Role,
                        Text = message.Text,
                        CreatedAt = message.CreatedAt,
                    });
                }

                TechnicalAnswer = Messages.LastOrDefault(static item => item.Role == "assistant")?.Text ?? string.Empty;
                ReviewAnswer = string.Empty;
                ThreadId = previous.CodexThreadId;
                ActualModel = previous.Model;
                ActualReasoningEffort = ValueOrDash(previous.ReasoningEffort);
                if (!string.IsNullOrWhiteSpace(previous.Model))
                {
                    Model = previous.Model;
                }
                hasSentInitialContext = previous.Messages.Count > 0;
                ConnectionDetails = "保存済みのチャット履歴を復元しました。続ける場合は「前回の続きから再開」を押してください。";
            }

            PreviousSessionStatus = load.Warning
                ?? (hasPreviousSession
                    ? $"前回Threadのチャット履歴を復元しました: {previous!.LastUsedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
                    : "この案件の前回Threadはありません。");
            RaiseCommandStates();
        });
    }

    private CodexSession CreateSession(
        CodexCaseSnapshot snapshot,
        CodexThreadStartResult thread,
        string requestedReasoningEffort)
    {
        return new CodexSession
        {
            SupportId = snapshot.SupportId,
            ProductId = snapshot.ProductId,
            CompanyName = snapshot.CompanyName,
            CaseFolder = snapshot.CaseFolder,
            CodexThreadId = thread.ThreadId,
            CreatedAt = DateTimeOffset.Now,
            LastUsedAt = DateTimeOffset.Now,
            CodexVersion = Version,
            Model = thread.Model,
            ReasoningEffort = string.IsNullOrWhiteSpace(thread.ReasoningEffort)
                ? requestedReasoningEffort
                : thread.ReasoningEffort,
            SessionStatus = "ready",
        };
    }

    private async Task PersistSessionAsync(string status)
    {
        if (currentSession is null)
        {
            return;
        }

        IReadOnlyList<CodexSessionMessage> messages = [];
        RunOnUi(() => messages = Messages
            .Where(static item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(static item => new CodexSessionMessage
            {
                Role = item.Role,
                Text = item.Text,
                CreatedAt = item.CreatedAt,
            })
            .ToArray());
        currentSession = currentSession with
        {
            LastUsedAt = DateTimeOffset.Now,
            LastTurnId = client.CurrentTurnId ?? currentSession.LastTurnId,
            CodexVersion = Version,
            Model = string.IsNullOrWhiteSpace(ActualModel) || ActualModel == "-" ? Model : ActualModel,
            ReasoningEffort = string.IsNullOrWhiteSpace(ActualReasoningEffort) || ActualReasoningEffort == "-"
                ? currentSession.ReasoningEffort
                : ActualReasoningEffort,
            SessionStatus = status,
            Messages = messages,
        };
        await sessionStore.SaveAsync(currentSession).ConfigureAwait(false);
    }

    private void ApplyLatestReply()
    {
        var value = LatestAnswer();
        if (currentSnapshot?.UseRagLabEvidence == true && ContainsRagLabInternalMarker(value))
        {
            WarningText = "お客様向け回答にRAG Labの内部処理用語が含まれているため、返信案への反映を中止しました。Codexで最終レビューしてください。";
            ConnectionDetails = "返信案へ反映していません。内部処理用語を除去してから再確認してください。";
            return;
        }
        if (applyReply(value))
        {
            ConnectionDetails = "回答を返信案編集欄へ反映しました。案件ファイルはまだ変更していません。";
            RaiseCommandStates();
        }
    }

    private async Task SendLatestAnswerToWpfNoteAsync()
    {
        var value = LatestAnswer();
        if (currentSnapshot?.UseRagLabEvidence == true && ContainsRagLabInternalMarker(value))
        {
            WarningText = "WPFノート編集へのコピーを中止しました。RAG Labの内部処理用語が含まれているため、Codexで最終レビューしてください。";
            ConnectionDetails = "WPFノート編集へコピーしていません。内部処理用語を除去してから再確認してください。";
            return;
        }

        var pipeName = currentSnapshot?.NoteEditorTransferPipeName;
        if (string.IsNullOrWhiteSpace(pipeName) || sendToWpfNoteEditor is null)
        {
            ConnectionDetails = "親WPFとの接続情報がありません。親WPFからAI回答支援を起動してください。";
            WarningText = "WPFノート編集へ送信できません。";
            return;
        }

        var sent = await sendToWpfNoteEditor(value).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (sent)
            {
                WarningText = string.Empty;
                ConnectionDetails = "回答をWPFノート編集へコピーしました。プレビューは解除され、案件ファイルはまだ変更していません。";
            }
            else
            {
                WarningText = "WPFノート編集へ送信できませんでした。親WPFが起動中か確認してください。";
                ConnectionDetails = "WPFノート編集へコピーしていません。";
            }
        });
    }

    private static bool ContainsRagLabInternalMarker(string value)
    {
        return RagLabInternalMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyLatestMemo()
    {
        var value = LatestAnswer();
        if (applyMemo(value))
        {
            ConnectionDetails = "回答を調査メモ編集欄へ反映しました。案件ファイルはまだ変更していません。";
            RaiseCommandStates();
        }
    }

    private void CopyLatestAnswer()
    {
        var value = LatestAnswer();
        if (!string.IsNullOrWhiteSpace(value))
        {
            WpfClipboard.SetText(value);
            ConnectionDetails = "Codex回答をクリップボードへコピーしました。";
        }
    }

    private void CaptureAbBaseline()
    {
        if (latestCompletedAbSample is null || latestCompletedAbSample.RagLabEvidence.Count > 0)
        {
            return;
        }

        baselineAbSample = latestCompletedAbSample with { Variant = "A" };
        BaselineAbStatus = BuildAbCaptureStatus(baselineAbSample);
        AbComparisonText = "Aを記録しました。Evidence付き回答Bを記録すると比較できます。";
        RaiseCommandStates();
    }

    private void CaptureAbEvidence()
    {
        if (latestCompletedAbSample is null || latestCompletedAbSample.RagLabEvidence.Count == 0)
        {
            return;
        }

        evidenceAbSample = latestCompletedAbSample with { Variant = "B" };
        EvidenceAbStatus = BuildAbCaptureStatus(evidenceAbSample);
        AbComparisonText = "Bを記録しました。AとBが同一問い合わせの場合に比較できます。";
        RaiseCommandStates();
    }

    private async Task CompareAbAsync()
    {
        if (baselineAbSample is null || evidenceAbSample is null)
        {
            return;
        }

        var result = abComparisonService.Compare(
            baselineAbSample,
            evidenceAbSample,
            [currentSnapshot?.ProductName ?? string.Empty]);
        RunOnUi(() => AbComparisonText = BuildAbComparisonText(result));
        await logger.WriteAsync(
            "rag-lab-ab-comparison",
            $"A/B metrics only. A_answerability={result.Baseline.Answerability} A_evidence={result.Baseline.UsedEvidenceCount} "
            + $"A_chars={result.Baseline.AnswerLength} A_confirmations={result.Baseline.ConfirmationCount} A_ms={result.Baseline.GenerationMilliseconds} "
            + $"B_answerability={result.WithEvidence.Answerability} B_evidence={result.WithEvidence.UsedEvidenceCount} "
            + $"B_chars={result.WithEvidence.AnswerLength} B_confirmations={result.WithEvidence.ConfirmationCount} B_ms={result.WithEvidence.GenerationMilliseconds} "
            + $"technical_added={result.TechnicalValueDiff.AddedValues.Count} technical_removed={result.TechnicalValueDiff.RemovedValues.Count} "
            + $"conflicts={result.WithEvidence.EvidenceConflictCount} unverified_fields={result.WithEvidence.UnverifiedEvidenceFieldCount}")
            .ConfigureAwait(false);
    }

    private bool CanCaptureAbBaseline() =>
        !turnActive && latestCompletedAbSample is { RagLabEvidence.Count: 0 };

    private bool CanCaptureAbEvidence() =>
        !turnActive && latestCompletedAbSample is { RagLabEvidence.Count: > 0 };

    private bool CanCompareAb() =>
        !turnActive
        && baselineAbSample is not null
        && evidenceAbSample is not null
        && string.Equals(baselineAbSample.ComparisonKey, evidenceAbSample.ComparisonKey, StringComparison.Ordinal);

    private static string BuildAbCaptureStatus(CodexAbAnswerSample sample) =>
        $"記録済み / 追加Evidence: {sample.RagLabEvidence.Count}件 / 生成時間: {Math.Max(0, sample.GenerationDuration.TotalSeconds):0.0}秒";

    private static string BuildAbComparisonText(CodexEvidenceAbComparisonResult result)
    {
        var builder = new StringBuilder();
        AppendAbMetrics(builder, result.Baseline);
        builder.AppendLine();
        AppendAbMetrics(builder, result.WithEvidence);
        builder.AppendLine();
        builder.AppendLine("技術値・コマンド差分 (B - A):");
        builder.AppendLine(result.TechnicalValueDiff.AddedValues.Count == 0
            ? "  追加: なし"
            : $"  追加: {string.Join(", ", result.TechnicalValueDiff.AddedValues)}");
        builder.AppendLine(result.TechnicalValueDiff.RemovedValues.Count == 0
            ? "  削除: なし"
            : $"  削除: {string.Join(", ", result.TechnicalValueDiff.RemovedValues)}");
        builder.AppendLine();
        builder.AppendLine("品質確認:");
        builder.AppendLine("- 質問へ直接回答しているか: 要手動確認");
        builder.AppendLine($"- 具体的な手順を検出: A={YesNo(result.Baseline.HasConcreteSteps)} / B={YesNo(result.WithEvidence.HasConcreteSteps)}");
        builder.AppendLine($"- 製品不一致の追加根拠: B={result.WithEvidence.ProductMismatchCount}件");
        builder.AppendLine($"- バージョン不一致の追加根拠: B={result.WithEvidence.VersionMismatchCount}件");
        builder.AppendLine($"- 根拠の矛盾警告: B={result.WithEvidence.EvidenceConflictCount}件");
        builder.AppendLine($"- 未確認フィールド: B={result.WithEvidence.UnverifiedEvidenceFieldCount}件 / 断定有無は要手動確認");
        builder.AppendLine($"- お客様向け日本語を検出: A={YesNo(result.Baseline.HasJapaneseText)} / B={YesNo(result.WithEvidence.HasJapaneseText)}");
        builder.AppendLine($"- 内部RAG用語を検出: A={YesNo(result.Baseline.ContainsInternalRagTerms)} / B={YesNo(result.WithEvidence.ContainsInternalRagTerms)}");
        builder.AppendLine($"判定: {result.QualityDecision}");
        return builder.ToString().TrimEnd();
    }

    private static void AppendAbMetrics(StringBuilder builder, CodexAbVariantMetrics metrics)
    {
        builder.AppendLine($"{metrics.Variant}: 回答可能判定={AnswerabilityText(metrics.Answerability)}");
        builder.AppendLine($"  使用根拠={metrics.UsedEvidenceCount}件 (公式={metrics.OfficialCount}, Manual={metrics.ManualCount}, PastCase={metrics.PastCaseCount}, その他={metrics.OtherEvidenceCount})");
        builder.AppendLine($"  回答文字数={metrics.AnswerLength} / 要確認事項={metrics.ConfirmationCount} / 生成時間={metrics.GenerationMilliseconds}ms");
    }

    private static string AnswerabilityText(CodexAbAnswerability value) => value switch
    {
        CodexAbAnswerability.Answerable => "回答あり",
        CodexAbAnswerability.InsufficientEvidence => "根拠不足表現あり",
        _ => "回答なし",
    };

    private static string YesNo(bool value) => value ? "あり" : "なし";

    private async Task EnsureConnectedAsync()
    {
        if (client.ConnectionInfo is null)
        {
            await ConnectAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteGuardedAsync(Func<Task> action)
    {
        try
        {
            ErrorText = string.Empty;
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await logger.WriteAsync("ui-operation", "Codex operation failed.", ex).ConfigureAwait(false);
            RunOnUi(() =>
            {
                ErrorText = $"処理に失敗しました: {ex.Message}{Environment.NewLine}現在の状態: {ConnectionStateText}{Environment.NewLine}再接続または再試行が可能です。{Environment.NewLine}診断ログ: {DiagnosticsPath}";
                ConnectionDetails = "Codex処理でエラーが発生しました。画面の案内を確認してください。";
                turnActive = false;
                RaiseCommandStates();
            });
        }
    }

    private bool CanSend() => CanStartThread() && !string.IsNullOrWhiteSpace(PromptInput);
    private bool CanStartThread() => !turnActive && caseFolderReady;
    private bool HasLatestAnswer() => !string.IsNullOrWhiteSpace(LatestAnswer());
    private bool CanSendToWpfNote() => !turnActive
        && HasLatestAnswer()
        && !string.IsNullOrWhiteSpace(currentSnapshot?.NoteEditorTransferPipeName)
        && sendToWpfNoteEditor is not null;
    private bool CanFinalReview() => !turnActive && HasLatestAnswer() && !string.IsNullOrWhiteSpace(client.CurrentThreadId);
    private string LatestAnswer() => string.IsNullOrWhiteSpace(ReviewAnswer) ? TechnicalAnswer : ReviewAnswer;

    private void RaiseCommandStates()
    {
        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        StartNewCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
        ReconnectCommand.RaiseCanExecuteChanged();
        RefreshFilesCommand.RaiseCanExecuteChanged();
        ApplyReplyCommand.RaiseCanExecuteChanged();
        SendToWpfNoteCommand.RaiseCanExecuteChanged();
        ApplyMemoCommand.RaiseCanExecuteChanged();
        UndoReplyCommand.RaiseCanExecuteChanged();
        UndoMemoCommand.RaiseCanExecuteChanged();
        CopyAnswerCommand.RaiseCanExecuteChanged();
        FinalReviewCommand.RaiseCanExecuteChanged();
        CaptureAbBaselineCommand.RaiseCanExecuteChanged();
        CaptureAbEvidenceCommand.RaiseCanExecuteChanged();
        CompareAbCommand.RaiseCanExecuteChanged();
        RaiseArtifactCommandStates();
        OnPropertyChanged(nameof(SelectedFilesSummary));
    }

    private static string BuildDifferenceText(CodexTechnicalValueDiff diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine("追加された技術値:");
        foreach (var value in diff.AddedValues)
        {
            builder.AppendLine($"+ {value}");
        }
        if (diff.AddedValues.Count == 0)
        {
            builder.AppendLine("(なし)");
        }
        builder.AppendLine("削除された技術値:");
        foreach (var value in diff.RemovedValues)
        {
            builder.AppendLine($"- {value}");
        }
        if (diff.RemovedValues.Count == 0)
        {
            builder.AppendLine("(なし)");
        }
        return builder.ToString().TrimEnd();
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ValueOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static void RunOnUi(Action action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
