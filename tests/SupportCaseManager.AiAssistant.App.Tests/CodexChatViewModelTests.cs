using System.Text;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class CodexChatViewModelTests
{
    [Fact]
    public async Task ConnectCommand_WhenIdle_DoesNotShowFalseProgress()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var viewModel = CreateViewModel(temp, fakeClient);

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == CodexConnectionState.Connected, TimeSpan.FromSeconds(5));

        Assert.Equal(0, viewModel.ProgressPercent);
        Assert.Equal("接続済み・調査待ち", viewModel.ProgressText);
    }

    [Fact]
    public async Task SendCommand_JoinsStreamingDeltasAndApplyDoesNotWriteFiles()
    {
        using var temp = new TempDirectory();
        var caseFile = Path.Combine(temp.Path, "case-note.txt");
        File.WriteAllText(caseFile, "original");
        var fakeClient = new FakeClient();
        string? applied = null;
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "HelixQAC",
                SupportId = "0001",
                CaseFolder = temp.Path,
                InquiryText = "問い合わせ",
            },
            () => "fake.exe",
            text => { applied = text; return true; },
            _ => true,
            _ => { });
        viewModel.PromptInput = "調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));
        viewModel.ApplyReplyCommand.Execute(null);

        Assert.Equal("回答です。", applied);
        Assert.Equal("original", File.ReadAllText(caseFile));
        Assert.Equal("回答です。", viewModel.Messages.Last().Text);
        Assert.False(viewModel.Messages.Last().IsStreaming);
    }

    [Fact]
    public async Task SendCommand_NormalizesLegacyLogAndSendsImageInput()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var temp = new TempDirectory();
        var logPath = Path.Combine(temp.Path, "trace.log");
        var imagePath = Path.Combine(temp.Path, "error-screen.png");
        await File.WriteAllTextAsync(logPath, "権限が不足しています。エラーコード: E_UPLOAD_42", Encoding.GetEncoding(932));
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);
        var fakeClient = new FakeClient();
        var viewModel = CreateViewModel(temp, fakeClient);
        viewModel.PromptInput = "原因を調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        Assert.Contains("権限が不足しています", fakeClient.LastTurnText);
        Assert.Contains("E_UPLOAD_42", fakeClient.LastTurnText);
        Assert.Contains("CP932", fakeClient.LastTurnText);
        Assert.Contains(imagePath, fakeClient.LastImagePaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("本文読取済み (UTF-8正規化)", viewModel.Files.Single(file => file.FullPath == logPath).ConfirmationStatus);
        Assert.Equal("画像入力として送信", viewModel.Files.Single(file => file.FullPath == imagePath).ConfirmationStatus);
    }

    [Fact]
    public async Task Initialize_WhenCaseFolderIsMissing_DisablesThreadAndSendCommandsWithReason()
    {
        using var temp = new TempDirectory();
        var missingFolder = Path.Combine(temp.Path, "missing");
        var fakeClient = new FakeClient();
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "Checkmarx",
                SupportId = "00018249",
                CaseFolder = missingFolder,
                InquiryText = "問い合わせ",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { });
        viewModel.PromptInput = "調査してください";

        await viewModel.InitializeAsync();

        Assert.False(viewModel.SendCommand.CanExecute(null));
        Assert.False(viewModel.StartNewCommand.CanExecute(null));
        Assert.Contains("送信できません", viewModel.CaseFolderSendStatus);
        Assert.Contains("案件フォルダが見つかりません", viewModel.CaseFolderSendStatus);
    }

    [Fact]
    public async Task Initialize_WhenCaseFolderWasRenamed_RestoresSavedChatImmediately()
    {
        using var temp = new TempDirectory();
        var productId = Guid.NewGuid();
        var movedFolder = Path.Combine(temp.Path, "00018250_メーカー確認中");
        Directory.CreateDirectory(movedFolder);
        var sessionStore = new CodexSessionStore(Path.Combine(temp.Path, "sessions.json"));
        await sessionStore.SaveAsync(new CodexSession
        {
            SupportId = "00018250",
            ProductId = productId,
            CaseFolder = Path.Combine(temp.Path, "00018250_受付"),
            CodexThreadId = "saved-thread",
            Model = "saved-model",
            LastUsedAt = DateTimeOffset.Now,
            Messages =
            [
                new CodexSessionMessage { Role = "user", Text = "前回の質問", CreatedAt = DateTimeOffset.Now.AddMinutes(-1) },
                new CodexSessionMessage { Role = "assistant", Text = "前回の回答", CreatedAt = DateTimeOffset.Now },
            ],
        });
        var viewModel = new CodexChatViewModel(
            new FakeClient(),
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            sessionStore,
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductId = productId,
                ProductName = "Checkmarx",
                SupportId = "00018250",
                CaseFolder = movedFolder,
                InquiryText = "問い合わせ",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { });

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("前回の質問", viewModel.Messages[0].Text);
        Assert.Equal("前回の回答", viewModel.TechnicalAnswer);
        Assert.Equal("saved-thread", viewModel.ThreadId);
        Assert.Equal("saved-model", viewModel.Model);
        Assert.True(viewModel.ResumeCommand.CanExecute(null));
        Assert.Contains("チャット履歴を復元しました", viewModel.PreviousSessionStatus);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition() && DateTime.UtcNow - started < timeout)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private static CodexChatViewModel CreateViewModel(TempDirectory temp, FakeClient fakeClient)
    {
        return new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "HelixQAC",
                SupportId = "0001",
                CaseFolder = temp.Path,
                InquiryText = "確認してください。",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { });
    }

    private sealed class FakeClient : ICodexAppServerClient
    {
        public event EventHandler<CodexConnectionState>? StateChanged;
        public event EventHandler<CodexAgentMessageDeltaEventArgs>? AgentMessageDelta;
        public event EventHandler<CodexTurnCompletedEventArgs>? TurnCompleted;
        public event EventHandler<CodexItemEventArgs>? ItemStarted { add { } remove { } }
        public event EventHandler<CodexItemEventArgs>? ItemCompleted { add { } remove { } }
        public event EventHandler<string>? Warning { add { } remove { } }
        public event EventHandler<string>? Error { add { } remove { } }

        public CodexConnectionState State { get; private set; } = CodexConnectionState.Disconnected;
        public CodexConnectionInfo? ConnectionInfo { get; private set; }
        public string? CurrentThreadId { get; private set; }
        public string? CurrentTurnId { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public string LastTurnText { get; private set; } = string.Empty;
        public IReadOnlyList<string> LastImagePaths { get; private set; } = [];

        public Task<CodexConnectionInfo> ConnectAsync(string? configuredExecutablePath, CancellationToken cancellationToken = default)
        {
            ConnectionInfo = new CodexConnectionInfo(
                "fake.exe",
                "0.145.0",
                "fake",
                new CodexAccountInfo { AccountType = "chatgpt", PlanType = "plus" },
                [new CodexModelInfo("fake", "Fake", true, false)]);
            State = CodexConnectionState.Connected;
            StateChanged?.Invoke(this, State);
            return Task.FromResult(ConnectionInfo);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = CodexConnectionState.Disconnected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task<CodexThreadStartResult> StartThreadAsync(string workingDirectory, string? model, CancellationToken cancellationToken = default)
        {
            CurrentThreadId = "thread-1";
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new CodexThreadStartResult("thread-1", "fake", workingDirectory, "read-only"));
        }

        public Task<CodexThreadStartResult> ResumeThreadAsync(string threadId, string workingDirectory, string? model, CancellationToken cancellationToken = default)
        {
            CurrentThreadId = threadId;
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new CodexThreadStartResult(threadId, "fake", workingDirectory, "read-only"));
        }

        public Task<CodexTurnStartResult> StartTurnAsync(string text, IReadOnlyList<string>? localImagePaths = null, CancellationToken cancellationToken = default)
        {
            LastTurnText = text;
            LastImagePaths = localImagePaths?.ToArray() ?? [];
            CurrentTurnId = "turn-1";
            AgentMessageDelta?.Invoke(this, new CodexAgentMessageDeltaEventArgs("thread-1", "turn-1", "item-1", "回答"));
            AgentMessageDelta?.Invoke(this, new CodexAgentMessageDeltaEventArgs("thread-1", "turn-1", "item-1", "です。"));
            CurrentTurnId = null;
            TurnCompleted?.Invoke(this, new CodexTurnCompletedEventArgs("thread-1", "turn-1", "completed", null));
            return Task.FromResult(new CodexTurnStartResult("turn-1"));
        }

        public Task InterruptTurnAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLogger(string path) : ICodexDiagnosticLogger
    {
        public string LogDirectory { get; } = path;
        public Task WriteAsync(string category, string message, Exception? exception = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexChatViewModelTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
