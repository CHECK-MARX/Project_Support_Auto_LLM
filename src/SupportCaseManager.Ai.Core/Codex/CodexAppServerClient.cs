using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record CodexConnectionInfo(
    string ExecutablePath,
    string Version,
    string UserAgent,
    CodexAccountInfo Account,
    IReadOnlyList<CodexModelInfo> Models);

public interface ICodexAppServerClient : IAsyncDisposable
{
    event EventHandler<CodexConnectionState>? StateChanged;
    event EventHandler<CodexAgentMessageDeltaEventArgs>? AgentMessageDelta;
    event EventHandler<CodexTurnCompletedEventArgs>? TurnCompleted;
    event EventHandler<CodexItemEventArgs>? ItemStarted;
    event EventHandler<CodexItemEventArgs>? ItemCompleted;
    event EventHandler<string>? Warning;
    event EventHandler<string>? Error;

    CodexConnectionState State { get; }
    CodexConnectionInfo? ConnectionInfo { get; }
    string? CurrentThreadId { get; }
    string? CurrentTurnId { get; }
    string? WorkingDirectory { get; }

    Task<CodexConnectionInfo> ConnectAsync(string? configuredExecutablePath, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<CodexThreadStartResult> StartThreadAsync(string workingDirectory, string? model, CancellationToken cancellationToken = default);
    Task<CodexThreadStartResult> ResumeThreadAsync(string threadId, string workingDirectory, string? model, CancellationToken cancellationToken = default);
    Task<CodexTurnStartResult> StartTurnAsync(string text, IReadOnlyList<string>? localImagePaths = null, CancellationToken cancellationToken = default);
    Task InterruptTurnAsync(CancellationToken cancellationToken = default);
}

public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private const string SafetyInstructions = """
        このスレッドはサポート案件の読み取り専用調査です。
        作業ディレクトリは現在の案件フォルダだけです。案件フォルダ外を探索、参照しないでください。
        ファイルの作成、更新、削除、名前変更、移動、アプリ設定変更を禁止します。
        シェルコマンド、書き込みを伴うツール、ネットワークアクセス、外部サイト参照を禁止します。
        承認が必要な操作は実行せず、読み取れた根拠だけで回答してください。
        回答案、社内メモ、案件ノートを自動保存しないでください。結果はチャット本文だけに返してください。
        不明な事項は推測で断定せず、不足情報と確認方法を明記してください。
        """;

    private static readonly TimeSpan ProtocolTimeout = TimeSpan.FromSeconds(30);
    private readonly ICodexExecutableResolver executableResolver;
    private readonly ICodexJsonRpcTransport transport;
    private readonly ICodexDiagnosticLogger logger;
    private bool disposed;
    private CodexConnectionState state = CodexConnectionState.Disconnected;
    private string? lastCompletedTurnId;

    public CodexAppServerClient(
        ICodexExecutableResolver executableResolver,
        ICodexJsonRpcTransport transport,
        ICodexDiagnosticLogger logger)
    {
        this.executableResolver = executableResolver;
        this.transport = transport;
        this.logger = logger;
        transport.NotificationReceived += OnNotificationReceived;
        transport.ServerRequestReceived += OnServerRequestReceived;
        transport.ProtocolWarning += OnProtocolWarning;
        transport.ProcessExited += OnProcessExited;
    }

    public event EventHandler<CodexConnectionState>? StateChanged;
    public event EventHandler<CodexAgentMessageDeltaEventArgs>? AgentMessageDelta;
    public event EventHandler<CodexTurnCompletedEventArgs>? TurnCompleted;
    public event EventHandler<CodexItemEventArgs>? ItemStarted;
    public event EventHandler<CodexItemEventArgs>? ItemCompleted;
    public event EventHandler<string>? Warning;
    public event EventHandler<string>? Error;

    public CodexConnectionState State => state;
    public CodexConnectionInfo? ConnectionInfo { get; private set; }
    public string? CurrentThreadId { get; private set; }
    public string? CurrentTurnId { get; private set; }
    public string? WorkingDirectory { get; private set; }

    public async Task<CodexConnectionInfo> ConnectAsync(
        string? configuredExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (transport.IsRunning && ConnectionInfo is not null)
        {
            return ConnectionInfo;
        }

        SetState(CodexConnectionState.Connecting);
        try
        {
            var resolution = await executableResolver.ResolveAsync(configuredExecutablePath, cancellationToken).ConfigureAwait(false);
            if (!resolution.Found || resolution.ExecutablePath is null)
            {
                throw new FileNotFoundException(resolution.Message);
            }

            var version = await executableResolver.GetVersionAsync(resolution.ExecutablePath, cancellationToken).ConfigureAwait(false)
                ?? "取得できません";
            await transport.StartAsync(resolution.ExecutablePath, cancellationToken).ConfigureAwait(false);
            var initialize = await transport.SendRequestAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new { name = "support-case-manager", title = "SupportCaseManager", version = "1.0" },
                        capabilities = new { experimentalApi = true },
                    },
                    ProtocolTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            await transport.SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);

            var accountResult = await transport.SendRequestAsync(
                    "account/read",
                    new { refreshToken = false },
                    ProtocolTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var account = ParseAccount(accountResult);
            if (!account.IsChatGptAuthenticated)
            {
                SetState(CodexConnectionState.AuthenticationRequired);
                throw new InvalidOperationException(
                    account.RequiresOpenAiAuth
                        ? "Codex CLIでChatGPTへログインしてください。APIキー認証はこの機能では使用しません。"
                        : "ChatGPT認証を確認できませんでした。Codex CLIのログイン状態を確認してください。");
            }

            var modelsResult = await transport.SendRequestAsync(
                    "model/list",
                    new { includeHidden = false, limit = 100 },
                    ProtocolTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var models = ParseModels(modelsResult);
            ConnectionInfo = new CodexConnectionInfo(
                resolution.ExecutablePath,
                version,
                GetString(initialize, "userAgent") ?? "不明",
                account,
                models);
            SetState(CodexConnectionState.Connected);
            await logger.WriteAsync("connection", $"Connected. executable={resolution.ExecutablePath}, version={version}, models={models.Count}, account=chatgpt", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ConnectionInfo;
        }
        catch (Exception ex)
        {
            if (State != CodexConnectionState.AuthenticationRequired)
            {
                SetState(CodexConnectionState.Error);
            }

            await logger.WriteAsync("connection", "Connection failed.", ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await transport.StopAsync(cancellationToken).ConfigureAwait(false);
        ConnectionInfo = null;
        CurrentThreadId = null;
        CurrentTurnId = null;
        WorkingDirectory = null;
        SetState(CodexConnectionState.Disconnected);
    }

    public async Task<CodexThreadStartResult> StartThreadAsync(
        string workingDirectory,
        string? model,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (!CodexPathPolicy.TryNormalizeRoot(workingDirectory, out var root, out var error))
        {
            throw new InvalidOperationException(error);
        }

        SetState(CodexConnectionState.StartingThread);
        var response = await transport.SendRequestAsync(
                "thread/start",
                BuildThreadParameters(root, model),
                ProtocolTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var result = ParseThreadResult(response);
        CurrentThreadId = result.ThreadId;
        CurrentTurnId = null;
        WorkingDirectory = root;
        SetState(CodexConnectionState.Connected);
        await logger.WriteAsync("thread", $"Thread started. threadId={result.ThreadId}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CodexThreadStartResult> ResumeThreadAsync(
        string threadId,
        string workingDirectory,
        string? model,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (!CodexPathPolicy.TryNormalizeRoot(workingDirectory, out var root, out var error))
        {
            throw new InvalidOperationException(error);
        }

        SetState(CodexConnectionState.StartingThread);
        var response = await transport.SendRequestAsync(
                "thread/resume",
                new
                {
                    threadId,
                    cwd = root,
                    model = NullIfWhiteSpace(model),
                    approvalPolicy = "never",
                    sandbox = "read-only",
                    developerInstructions = SafetyInstructions,
                    runtimeWorkspaceRoots = new[] { root },
                },
                ProtocolTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var result = ParseThreadResult(response);
        CurrentThreadId = result.ThreadId;
        CurrentTurnId = null;
        WorkingDirectory = root;
        SetState(CodexConnectionState.Connected);
        await logger.WriteAsync("thread", $"Thread resumed. threadId={result.ThreadId}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CodexTurnStartResult> StartTurnAsync(
        string text,
        IReadOnlyList<string>? localImagePaths = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(CurrentThreadId) || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            throw new InvalidOperationException("Codex Threadが開始されていません。");
        }

        var inputs = new List<object>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            inputs.Add(new { type = "text", text });
        }

        foreach (var path in localImagePaths ?? [])
        {
            if (!CodexPathPolicy.TryNormalizeFileWithinRoot(WorkingDirectory, path, out var normalized, out var error))
            {
                throw new InvalidOperationException(error);
            }

            if (!CodexPathPolicy.IsSupportedImage(normalized))
            {
                throw new InvalidOperationException("画像入力として対応していないファイル形式です。");
            }

            inputs.Add(new { type = "localImage", path = normalized });
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("Codexへ送信する指示または画像を入力してください。");
        }

        SetState(CodexConnectionState.Investigating);
        var response = await transport.SendRequestAsync(
                "turn/start",
                new
                {
                    threadId = CurrentThreadId,
                    input = inputs,
                    approvalPolicy = "never",
                    cwd = WorkingDirectory,
                    runtimeWorkspaceRoots = new[] { WorkingDirectory },
                    environments = Array.Empty<object>(),
                },
                ProtocolTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var turnId = GetNestedString(response, "turn", "id")
            ?? throw new JsonException("turn/start応答にturn.idがありません。");
        CurrentTurnId = string.Equals(lastCompletedTurnId, turnId, StringComparison.Ordinal) ? null : turnId;
        await logger.WriteAsync("turn", $"Turn started. threadId={CurrentThreadId}, turnId={turnId}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return new CodexTurnStartResult(turnId);
    }

    public async Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentThreadId) || string.IsNullOrWhiteSpace(CurrentTurnId))
        {
            return;
        }

        SetState(CodexConnectionState.Interrupting);
        await transport.SendRequestAsync(
                "turn/interrupt",
                new { threadId = CurrentThreadId, turnId = CurrentTurnId },
                ProtocolTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        await logger.WriteAsync("turn", "Turn interrupted.", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        transport.NotificationReceived -= OnNotificationReceived;
        transport.ServerRequestReceived -= OnServerRequestReceived;
        transport.ProtocolWarning -= OnProtocolWarning;
        transport.ProcessExited -= OnProcessExited;
        await transport.DisposeAsync().ConfigureAwait(false);
    }

    private object BuildThreadParameters(string root, string? model)
    {
        return new
        {
            cwd = root,
            model = NullIfWhiteSpace(model),
            approvalPolicy = "never",
            sandbox = "read-only",
            developerInstructions = SafetyInstructions,
            runtimeWorkspaceRoots = new[] { root },
            environments = Array.Empty<object>(),
            ephemeral = false,
        };
    }

    private async void OnServerRequestReceived(object? sender, CodexProtocolMessageEventArgs eventArgs)
    {
        var message = eventArgs.Message;
        if (message.Id is null || message.Method is null)
        {
            return;
        }

        try
        {
            switch (message.Method)
            {
                case "item/commandExecution/requestApproval":
                case "item/fileChange/requestApproval":
                    await transport.SendResponseAsync(message.Id, new { decision = "cancel" }).ConfigureAwait(false);
                    Warning?.Invoke(this, "Codexからの実行・書き込み承認要求を読み取り専用ポリシーにより拒否しました。");
                    break;
                case "item/permissions/requestApproval":
                    await transport.SendResponseAsync(
                            message.Id,
                            error: new CodexJsonRpcError(-32000, "Permission request denied by read-only client policy."))
                        .ConfigureAwait(false);
                    Warning?.Invoke(this, "Codexからの追加権限要求を読み取り専用ポリシーにより拒否しました。");
                    break;
                default:
                    await transport.SendResponseAsync(
                            message.Id,
                            error: new CodexJsonRpcError(-32601, $"Unsupported server request: {message.Method}"))
                        .ConfigureAwait(false);
                    Warning?.Invoke(this, $"未対応のCodex要求を拒否しました: {message.Method}");
                    break;
            }

            await logger.WriteAsync("approval", $"Rejected server request method={message.Method}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await logger.WriteAsync("approval", "Failed to reject server request.", ex).ConfigureAwait(false);
            Error?.Invoke(this, "Codexの権限要求を拒否する処理でエラーが発生しました。");
        }
    }

    private void OnNotificationReceived(object? sender, CodexProtocolMessageEventArgs eventArgs)
    {
        var message = eventArgs.Message;
        if (message.Method is null || message.Params is not JsonElement parameters)
        {
            return;
        }

        try
        {
            switch (message.Method)
            {
                case "item/agentMessage/delta":
                    AgentMessageDelta?.Invoke(this, new CodexAgentMessageDeltaEventArgs(
                        RequiredString(parameters, "threadId"),
                        RequiredString(parameters, "turnId"),
                        RequiredString(parameters, "itemId"),
                        RequiredString(parameters, "delta")));
                    SetState(CodexConnectionState.GeneratingAnswer);
                    break;
                case "item/started":
                    ItemStarted?.Invoke(this, ParseItem(parameters));
                    break;
                case "item/completed":
                    ItemCompleted?.Invoke(this, ParseItem(parameters));
                    break;
                case "turn/completed":
                    var completed = ParseTurnCompleted(parameters);
                    lastCompletedTurnId = completed.TurnId;
                    CurrentTurnId = null;
                    SetState(string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase)
                        ? CodexConnectionState.Completed
                        : CodexConnectionState.Error);
                    TurnCompleted?.Invoke(this, completed);
                    break;
                case "error":
                    var errorMessage = GetString(parameters, "message") ?? "Codexでエラーが発生しました。";
                    SetState(CodexConnectionState.Error);
                    Error?.Invoke(this, errorMessage);
                    break;
                case "thread/started":
                case "thread/status/changed":
                case "turn/started":
                case "thread/tokenUsage/updated":
                case "account/updated":
                case "account/rateLimits/updated":
                    break;
                default:
                    Warning?.Invoke(this, $"未対応のCodex通知を受信しました。処理は継続します: {message.Method}");
                    _ = logger.WriteAsync("notification", $"Unknown method={message.Method}");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Warning?.Invoke(this, $"Codex通知を解析できませんでした: {message.Method}");
            _ = logger.WriteAsync("notification", $"Parse failed. method={message.Method}", ex);
        }
    }

    private void OnProtocolWarning(object? sender, CodexProtocolWarningEventArgs eventArgs)
    {
        Warning?.Invoke(this, eventArgs.Message);
    }

    private void OnProcessExited(object? sender, CodexProcessExitedEventArgs eventArgs)
    {
        ConnectionInfo = null;
        CurrentTurnId = null;
        if (!eventArgs.WasExpected)
        {
            SetState(CodexConnectionState.ReconnectRequired);
            Error?.Invoke(this, $"Codex App Serverが予期せず終了しました（終了コード: {eventArgs.ExitCode?.ToString() ?? "不明"}）。再接続してください。");
        }
    }

    private static CodexAccountInfo ParseAccount(JsonElement result)
    {
        var requiresAuth = result.TryGetProperty("requiresOpenaiAuth", out var requiresElement)
            && requiresElement.ValueKind == JsonValueKind.True;
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return new CodexAccountInfo { RequiresOpenAiAuth = requiresAuth };
        }

        return new CodexAccountInfo
        {
            RequiresOpenAiAuth = requiresAuth,
            AccountType = GetString(account, "type") ?? string.Empty,
            PlanType = GetString(account, "planType") ?? string.Empty,
        };
    }

    private static IReadOnlyList<CodexModelInfo> ParseModels(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new CodexModelInfo(
                GetString(item, "id") ?? GetString(item, "model") ?? string.Empty,
                GetString(item, "displayName") ?? GetString(item, "model") ?? string.Empty,
                GetBoolean(item, "isDefault"),
                GetBoolean(item, "hidden")))
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .ToArray();
    }

    private static CodexThreadStartResult ParseThreadResult(JsonElement response)
    {
        return new CodexThreadStartResult(
            GetNestedString(response, "thread", "id")
                ?? throw new JsonException("Thread応答にthread.idがありません。"),
            GetString(response, "model") ?? string.Empty,
            GetString(response, "cwd") ?? string.Empty,
            ReadSandbox(response));
    }

    private static string ReadSandbox(JsonElement response)
    {
        if (!response.TryGetProperty("sandbox", out var sandbox))
        {
            return string.Empty;
        }

        if (sandbox.ValueKind == JsonValueKind.String)
        {
            return sandbox.GetString() ?? string.Empty;
        }

        return sandbox.ValueKind == JsonValueKind.Object
            ? GetString(sandbox, "type") ?? sandbox.GetRawText()
            : string.Empty;
    }

    private static CodexItemEventArgs ParseItem(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("item通知にitemがありません。");
        }

        return new CodexItemEventArgs(
            RequiredString(parameters, "threadId"),
            RequiredString(parameters, "turnId"),
            GetString(item, "id") ?? string.Empty,
            GetString(item, "type") ?? "unknown",
            GetString(item, "text") ?? GetString(item, "message"),
            GetString(item, "path"),
            GetString(item, "status"));
    }

    private static CodexTurnCompletedEventArgs ParseTurnCompleted(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("turn", out var turn) || turn.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("turn/completed通知にturnがありません。");
        }

        string? error = null;
        if (turn.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            error = GetString(errorElement, "message");
        }

        return new CodexTurnCompletedEventArgs(
            RequiredString(parameters, "threadId"),
            RequiredString(turn, "id"),
            RequiredString(turn, "status"),
            error);
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        return GetString(element, propertyName)
            ?? throw new JsonException($"必須プロパティがありません: {propertyName}");
    }

    private static string? GetNestedString(JsonElement element, string parent, string child)
    {
        return element.TryGetProperty(parent, out var parentElement) && parentElement.ValueKind == JsonValueKind.Object
            ? GetString(parentElement, child)
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!transport.IsRunning || ConnectionInfo is null)
        {
            throw new InvalidOperationException("Codex App Serverへ接続してください。");
        }
    }

    private void SetState(CodexConnectionState value)
    {
        if (state == value)
        {
            return;
        }

        state = value;
        StateChanged?.Invoke(this, value);
    }
}
