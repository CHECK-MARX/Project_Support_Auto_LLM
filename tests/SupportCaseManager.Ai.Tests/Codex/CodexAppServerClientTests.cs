using System.Text.Json;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task ConnectThreadTurnStreamingCompletionAndInterrupt_UseVersion145Methods()
    {
        using var temp = new Helpers.TempDirectory();
        var transport = CreateConnectedTransport();
        transport.Enqueue("thread/start", """{"thread":{"id":"thread-1"},"model":"codex-default","cwd":"C:/case","sandbox":"read-only"}""");
        transport.Enqueue("turn/start", """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}""");
        transport.Enqueue("turn/interrupt", "{}");
        var client = CreateClient(transport);
        var deltas = new List<string>();
        CodexTurnCompletedEventArgs? completed = null;
        client.AgentMessageDelta += (_, args) => deltas.Add(args.Delta);
        client.TurnCompleted += (_, args) => completed = args;

        var connection = await client.ConnectAsync("codex.exe");
        var thread = await client.StartThreadAsync(temp.Path, null);
        var turn = await client.StartTurnAsync("調査してください");
        transport.EmitNotification("item/agentMessage/delta", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"回答"}""");
        transport.EmitNotification("item/agentMessage/delta", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"です"}""");
        await client.InterruptTurnAsync();
        transport.EmitNotification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[],"error":null}}""");

        Assert.True(connection.Account.IsChatGptAuthenticated);
        Assert.Equal("thread-1", thread.ThreadId);
        Assert.Equal("turn-1", turn.TurnId);
        Assert.Equal("回答です", string.Concat(deltas));
        Assert.Equal("completed", completed?.Status);
        Assert.Contains(transport.Requests, request => request.Method == "initialize");
        Assert.Contains(transport.Notifications, notification => notification.Method == "initialized");
        Assert.Contains(transport.Requests, request => request.Method == "turn/interrupt");
    }

    [Fact]
    public async Task ServerApprovalRequests_AreRejected()
    {
        var transport = CreateConnectedTransport();
        var client = CreateClient(transport);
        await client.ConnectAsync("codex.exe");

        transport.EmitServerRequest("11", "item/commandExecution/requestApproval", "{}");
        transport.EmitServerRequest("12", "item/fileChange/requestApproval", "{}");

        await WaitUntilAsync(() => transport.Responses.Count >= 2);
        Assert.All(transport.Responses, response => Assert.Contains("cancel", response.Json));
    }

    [Fact]
    public async Task UnknownNotification_IsReportedWithoutCrashing()
    {
        var transport = CreateConnectedTransport();
        var client = CreateClient(transport);
        string? warning = null;
        client.Warning += (_, value) => warning = value;
        await client.ConnectAsync("codex.exe");

        transport.EmitNotification("future/new-event", "{}");

        Assert.Contains("未対応", warning);
        Assert.Equal(CodexConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ConnectAsync_RejectsApiKeyAccount()
    {
        var transport = new FakeTransport();
        transport.Enqueue("initialize", """{"codexHome":"C:/codex","platformFamily":"windows","platformOs":"windows","userAgent":"codex/0.145.0"}""");
        transport.Enqueue("account/read", """{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}""");
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync("codex.exe"));

        Assert.Contains("ChatGPT", exception.Message);
        Assert.Equal(CodexConnectionState.AuthenticationRequired, client.State);
    }

    private static FakeTransport CreateConnectedTransport()
    {
        var transport = new FakeTransport();
        transport.Enqueue("initialize", """{"codexHome":"C:/codex","platformFamily":"windows","platformOs":"windows","userAgent":"codex/0.145.0"}""");
        transport.Enqueue("account/read", """{"account":{"type":"chatgpt","email":null,"planType":"plus"},"requiresOpenaiAuth":true}""");
        transport.Enqueue("model/list", """{"data":[{"id":"codex-default","model":"codex-default","displayName":"Codex Default","isDefault":true,"hidden":false}],"nextCursor":null}""");
        return transport;
    }

    private static CodexAppServerClient CreateClient(FakeTransport transport)
    {
        return new CodexAppServerClient(
            new FakeResolver(),
            transport,
            new FakeLogger());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var index = 0; index < 50 && !condition(); index++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class FakeResolver : ICodexExecutableResolver
    {
        public Task<CodexExecutableResolution> ResolveAsync(string? configuredPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexExecutableResolution
            {
                ExecutablePath = configuredPath ?? "codex.exe",
                Source = CodexExecutableSource.UserSetting,
            });

        public Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("codex-cli 0.145.0");
    }

    private sealed class FakeLogger : ICodexDiagnosticLogger
    {
        public string LogDirectory => "logs";
        public Task WriteAsync(string category, string message, Exception? exception = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTransport : ICodexJsonRpcTransport
    {
        private readonly Dictionary<string, Queue<JsonElement>> responses = new(StringComparer.Ordinal);
        public event EventHandler<CodexProtocolMessageEventArgs>? NotificationReceived;
        public event EventHandler<CodexProtocolMessageEventArgs>? ServerRequestReceived;
        public event EventHandler<CodexProtocolWarningEventArgs>? ProtocolWarning { add { } remove { } }
        public event EventHandler<string>? StandardErrorReceived { add { } remove { } }
        public event EventHandler<CodexProcessExitedEventArgs>? ProcessExited { add { } remove { } }
        public bool IsRunning { get; private set; }
        public List<(string Method, object? Parameters)> Requests { get; } = [];
        public List<(string Method, object? Parameters)> Notifications { get; } = [];
        public List<(string Id, string Json)> Responses { get; } = [];

        public void Enqueue(string method, string json)
        {
            if (!responses.TryGetValue(method, out var queue))
            {
                queue = new Queue<JsonElement>();
                responses[method] = queue;
            }
            queue.Enqueue(Parse(json));
        }

        public Task StartAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, parameters));
            if (!responses.TryGetValue(method, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"No fake response for {method}");
            }
            return Task.FromResult(queue.Dequeue());
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken = default)
        {
            Notifications.Add((method, parameters));
            return Task.CompletedTask;
        }

        public Task SendResponseAsync(string requestId, object? result = null, CodexJsonRpcError? error = null, CancellationToken cancellationToken = default)
        {
            Responses.Add((requestId, JsonSerializer.Serialize(result ?? error)));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitNotification(string method, string parameters)
        {
            NotificationReceived?.Invoke(this, new CodexProtocolMessageEventArgs(new CodexIncomingMessage
            {
                Method = method,
                Params = Parse(parameters),
            }));
        }

        public void EmitServerRequest(string id, string method, string parameters)
        {
            ServerRequestReceived?.Invoke(this, new CodexProtocolMessageEventArgs(new CodexIncomingMessage
            {
                Id = id,
                Method = method,
                Params = Parse(parameters),
            }));
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
