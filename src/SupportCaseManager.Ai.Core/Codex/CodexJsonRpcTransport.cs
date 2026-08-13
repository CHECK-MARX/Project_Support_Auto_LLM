using System.Collections.Concurrent;
using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record CodexProtocolMessageEventArgs(CodexIncomingMessage Message);
public sealed record CodexProtocolWarningEventArgs(string Message, Exception? Exception = null);

public interface ICodexJsonRpcTransport : IAsyncDisposable
{
    event EventHandler<CodexProtocolMessageEventArgs>? NotificationReceived;
    event EventHandler<CodexProtocolMessageEventArgs>? ServerRequestReceived;
    event EventHandler<CodexProtocolWarningEventArgs>? ProtocolWarning;
    event EventHandler<string>? StandardErrorReceived;
    event EventHandler<CodexProcessExitedEventArgs>? ProcessExited;

    bool IsRunning { get; }
    Task StartAsync(string executablePath, CancellationToken cancellationToken = default);
    Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken = default);
    Task SendResponseAsync(string requestId, object? result = null, CodexJsonRpcError? error = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class CodexJsonRpcTransport : ICodexJsonRpcTransport
{
    private readonly ICodexAppServerProcessHost processHost;
    private readonly ICodexDiagnosticLogger logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CodexIncomingMessage>> pending = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private long nextRequestId;
    private bool disposed;

    public CodexJsonRpcTransport(ICodexAppServerProcessHost processHost, ICodexDiagnosticLogger logger)
    {
        this.processHost = processHost;
        this.logger = logger;
        processHost.StandardErrorReceived += OnStandardErrorReceived;
        processHost.Exited += OnProcessExited;
    }

    public event EventHandler<CodexProtocolMessageEventArgs>? NotificationReceived;
    public event EventHandler<CodexProtocolMessageEventArgs>? ServerRequestReceived;
    public event EventHandler<CodexProtocolWarningEventArgs>? ProtocolWarning;
    public event EventHandler<string>? StandardErrorReceived;
    public event EventHandler<CodexProcessExitedEventArgs>? ProcessExited;

    public bool IsRunning => processHost.IsRunning;

    public async Task StartAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (processHost.IsRunning)
        {
            return;
        }

        await processHost.StartAsync(executablePath, cancellationToken).ConfigureAwait(false);
        readCancellation = new CancellationTokenSource();
        readTask = ReadLoopAsync(readCancellation.Token);
        await logger.WriteAsync("process", "Codex App Server started.", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var id = Interlocked.Increment(ref nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<CodexIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Codex要求IDを登録できませんでした。");
        }

        try
        {
            await WriteLineAsync(CodexProtocolSerializer.SerializeRequest(long.Parse(id), method, parameters), cancellationToken)
                .ConfigureAwait(false);
            await logger.WriteAsync("request", method, cancellationToken: cancellationToken).ConfigureAwait(false);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            var response = await completion.Task.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            if (response.Error is not null)
            {
                throw new CodexJsonRpcException(response.Error.Code, response.Error.Message);
            }

            return response.Result ?? JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Codex App Serverの応答待ちがタイムアウトしました: {method}");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        return WriteLineAsync(CodexProtocolSerializer.SerializeNotification(method, parameters), cancellationToken);
    }

    public Task SendResponseAsync(
        string requestId,
        object? result = null,
        CodexJsonRpcError? error = null,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        return WriteLineAsync(CodexProtocolSerializer.SerializeResponse(requestId, result, error), cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        readCancellation?.Cancel();
        await processHost.StopAsync(cancellationToken).ConfigureAwait(false);
        if (readTask is not null)
        {
            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        CancelPending(new IOException("Codex App Serverとの接続を終了しました。"));
        readCancellation?.Dispose();
        readCancellation = null;
        readTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
            await processHost.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            disposed = true;
            processHost.StandardErrorReceived -= OnStandardErrorReceived;
            processHost.Exited -= OnProcessExited;
            writeGate.Dispose();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await processHost.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                try
                {
                    var message = CodexProtocolSerializer.Deserialize(line);
                    if (message.IsResponse && message.Id is not null)
                    {
                        if (pending.TryGetValue(message.Id, out var completion))
                        {
                            completion.TrySetResult(message);
                        }
                        else
                        {
                            RaiseWarning("対応する要求がないCodex応答を受信しました。");
                        }
                    }
                    else if (message.IsServerRequest)
                    {
                        ServerRequestReceived?.Invoke(this, new CodexProtocolMessageEventArgs(message));
                    }
                    else if (message.IsNotification)
                    {
                        NotificationReceived?.Invoke(this, new CodexProtocolMessageEventArgs(message));
                    }
                    else
                    {
                        RaiseWarning("形式を判定できないCodexメッセージを受信しました。");
                    }
                }
                catch (JsonException ex)
                {
                    RaiseWarning("Codex App Serverから不正なJSONを受信しました。", ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            RaiseWarning("Codex App Serverの受信処理が停止しました。", ex);
            CancelPending(ex);
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await processHost.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await processHost.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private void OnStandardErrorReceived(object? sender, string line)
    {
        StandardErrorReceived?.Invoke(this, line);
        _ = logger.WriteAsync("stderr", line);
    }

    private void OnProcessExited(object? sender, CodexProcessExitedEventArgs eventArgs)
    {
        var message = $"Codex App Server exited. code={eventArgs.ExitCode?.ToString() ?? "unknown"}, expected={eventArgs.WasExpected}";
        _ = logger.WriteAsync("process", message);
        if (!eventArgs.WasExpected)
        {
            CancelPending(new IOException("Codex App Serverが予期せず終了しました。"));
        }

        ProcessExited?.Invoke(this, eventArgs);
    }

    private void RaiseWarning(string message, Exception? exception = null)
    {
        _ = logger.WriteAsync("protocol", message, exception);
        ProtocolWarning?.Invoke(this, new CodexProtocolWarningEventArgs(message, exception));
    }

    private void CancelPending(Exception exception)
    {
        foreach (var item in pending)
        {
            item.Value.TrySetException(exception);
        }
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!processHost.IsRunning)
        {
            throw new InvalidOperationException("Codex App Serverは起動していません。");
        }
    }
}
