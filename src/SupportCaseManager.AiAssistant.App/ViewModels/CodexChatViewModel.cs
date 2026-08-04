using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Core.Codex;
using WpfApplication = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed partial class CodexChatViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ICodexAppServerClient client;
    private readonly ICodexCaseFileScanner fileScanner;
    private readonly ICodexPromptComposer promptComposer;
    private readonly ICodexSessionStore sessionStore;
    private readonly ICodexTechnicalValueDiffDetector diffDetector;
    private readonly ICodexAttachmentContentReader attachmentContentReader;
    private readonly ICodexDiagnosticLogger logger;
    private readonly Func<CodexCaseSnapshot> caseProvider;
    private readonly Func<string> executablePathProvider;
    private readonly Func<string, bool> applyReply;
    private readonly Func<string, bool> applyMemo;
    private readonly Action<bool> undoApplication;
    private readonly IExcelTranslationService excelTranslationService;
    private readonly IArtifactPromptComposer artifactPromptComposer;
    private readonly ArtifactRequestDetector artifactRequestDetector;
    private readonly ExcelTranslationJsonParser translationJsonParser;
    private readonly CaseArtifactPathPolicy artifactPathPolicy;
    private CodexConnectionState connectionState = CodexConnectionState.Disconnected;
    private string connectionDetails = "Codexへ接続してください。";
    private string version = "-";
    private string model = "Codex側の既定モデル";
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
        CaseArtifactPathPolicy? artifactPathPolicy = null)
    {
        this.client = client;
        this.fileScanner = fileScanner;
        this.promptComposer = promptComposer;
        this.sessionStore = sessionStore;
        this.diffDetector = diffDetector;
        this.attachmentContentReader = attachmentContentReader ?? new CodexAttachmentContentReader();
        this.logger = logger;
        this.caseProvider = caseProvider;
        this.executablePathProvider = executablePathProvider;
        this.applyReply = applyReply;
        this.applyMemo = applyMemo;
        this.undoApplication = undoApplication;
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
        UndoReplyCommand = new RelayCommand(() => undoApplication(true));
        UndoMemoCommand = new RelayCommand(() => undoApplication(false));
        CopyAnswerCommand = new RelayCommand(CopyLatestAnswer, HasLatestAnswer);
        FinalReviewCommand = new AsyncRelayCommand(() => ExecuteGuardedAsync(FinalReviewAsync), () => !turnActive && HasLatestAnswer());
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
    public IReadOnlyList<CodexPromptPreset> PromptPresets { get; } = CodexPromptPreset.Defaults;

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public AsyncRelayCommand StartNewCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshFilesCommand { get; }
    public RelayCommand ApplyReplyCommand { get; }
    public RelayCommand ApplyMemoCommand { get; }
    public RelayCommand UndoReplyCommand { get; }
    public RelayCommand UndoMemoCommand { get; }
    public RelayCommand CopyAnswerCommand { get; }
    public AsyncRelayCommand FinalReviewCommand { get; }

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
        private set
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
        await RefreshFilesAsync().ConfigureAwait(false);
        await FindPreviousSessionAsync().ConfigureAwait(false);
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

    private async Task ConnectAsync()
    {
        ErrorText = string.Empty;
        var info = await client.ConnectAsync(executablePathProvider()).ConfigureAwait(false);
        RunOnUi(() =>
        {
            Version = info.Version;
            AccountStatus = $"ChatGPT認証済み / プラン: {ValueOrDash(info.Account.PlanType)}";
            var defaultModel = info.Models.FirstOrDefault(static item => item.IsDefault && !item.Hidden)
                ?? info.Models.FirstOrDefault(static item => !item.Hidden);
            Model = defaultModel is null
                ? "Codex側の既定モデル"
                : $"{defaultModel.DisplayName} ({defaultModel.Id})";
            ConnectionDetails = $"App Server利用可 / {info.UserAgent}";
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

        var result = await client.StartThreadAsync(snapshot.CaseFolder, model: null).ConfigureAwait(false);
        currentSnapshot = snapshot;
        currentSession = CreateSession(snapshot, result);
        RunOnUi(() =>
        {
            Messages.Clear();
            hasSentInitialContext = false;
            TechnicalAnswer = string.Empty;
            ReviewAnswer = string.Empty;
            ThreadId = result.ThreadId;
            Model = string.IsNullOrWhiteSpace(result.Model) ? Model : result.Model;
            ConnectionDetails = "新しい読み取り専用Threadを開始しました。指示を送信できます。";
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
                ConnectionDetails = "前回のCodex Threadを再開しました。追加質問を送信できます。";
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
        var selectedFiles = Files.Where(static file => file.IsSelected && file.CanSendToCodex).ToArray();
        RunOnUi(() => ConnectionDetails = "選択した添付ファイルを読み取り、文字コード変換と本文抽出を行っています。");
        var attachmentRead = await attachmentContentReader.ReadAsync(
            currentSnapshot.CaseFolder,
            selectedFiles.Select(static file => file.File).ToArray()).ConfigureAwait(false);
        string prompt;
        IReadOnlyList<string> compositionWarnings = [];
        if (firstTurn)
        {
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
                UserInstruction = instruction,
            });
            prompt = composition.Prompt;
            compositionWarnings = composition.Warnings;
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

        try
        {
            var turn = await client.StartTurnAsync(prompt, imagePaths).ConfigureAwait(false);
            hasSentInitialContext = true;
            if (currentSession is not null && turnActive)
            {
                currentSession = currentSession with { LastTurnId = turn.TurnId, LastUsedAt = DateTimeOffset.Now, SessionStatus = "running" };
                await PersistSessionAsync("running").ConfigureAwait(false);
            }
        }
        catch
        {
            RunOnUi(() =>
            {
                turnActive = false;
                assistantMessage.IsStreaming = false;
                RaiseCommandStates();
            });
            throw;
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

    private CodexSession CreateSession(CodexCaseSnapshot snapshot, CodexThreadStartResult thread)
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
            Model = Model,
            SessionStatus = status,
            Messages = messages,
        };
        await sessionStore.SaveAsync(currentSession).ConfigureAwait(false);
    }

    private void ApplyLatestReply()
    {
        var value = LatestAnswer();
        if (applyReply(value))
        {
            ConnectionDetails = "回答を返信案編集欄へ反映しました。案件ファイルはまだ変更していません。";
        }
    }

    private void ApplyLatestMemo()
    {
        var value = LatestAnswer();
        if (applyMemo(value))
        {
            ConnectionDetails = "回答を調査メモ編集欄へ反映しました。案件ファイルはまだ変更していません。";
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
        ApplyMemoCommand.RaiseCanExecuteChanged();
        CopyAnswerCommand.RaiseCanExecuteChanged();
        FinalReviewCommand.RaiseCanExecuteChanged();
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
