using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexFakeProcessIntegrationTests
{
    [Fact]
    public async Task FakeChildProcess_InitializeThreadTurnStreamAndShutdown()
    {
        using var temp = new TempDirectory();
        var (client, logger) = CreateClient(temp.Path);
        await using var ownedClient = client;
        var completion = new TaskCompletionSource<CodexTurnCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = string.Empty;
        client.AgentMessageDelta += (_, args) => output += args.Delta;
        client.TurnCompleted += (_, args) => completion.TrySetResult(args);

        var info = await client.ConnectAsync(FakeExecutablePath());
        await client.StartThreadAsync(temp.Path, null);
        await client.StartTurnAsync("通常テスト");
        var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.DisconnectAsync();

        Assert.Equal("fake-codex/0.145.0", info.UserAgent);
        Assert.Equal("Fake回答です。", output);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(CodexConnectionState.Disconnected, client.State);
        logger.Dispose();
    }

    [Fact]
    public async Task FakeChildProcess_InterruptAndAbnormalExitAreDetected()
    {
        using var temp = new TempDirectory();
        var (client, logger) = CreateClient(temp.Path);
        await using var ownedClient = client;
        var interrupted = new TaskCompletionSource<CodexTurnCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TurnCompleted += (_, args) => interrupted.TrySetResult(args);
        await client.ConnectAsync(FakeExecutablePath());
        await client.StartThreadAsync(temp.Path, null);
        await client.StartTurnAsync("WAIT_FOR_INTERRUPT");
        await client.InterruptTurnAsync();
        var completed = await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, state) =>
        {
            if (state == CodexConnectionState.ReconnectRequired)
            {
                exited.TrySetResult();
            }
        };
        await client.StartTurnAsync("EXIT_PROCESS");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("interrupted", completed.Status);
        Assert.Equal(CodexConnectionState.ReconnectRequired, client.State);
        logger.Dispose();
    }

    [Fact]
    public async Task FakeChildProcess_ReconnectAndResumeExistingThread()
    {
        using var temp = new TempDirectory();
        var (client, logger) = CreateClient(temp.Path);
        await using var ownedClient = client;

        await client.ConnectAsync(FakeExecutablePath());
        var started = await client.StartThreadAsync(temp.Path, null);
        await client.DisconnectAsync();

        await client.ConnectAsync(FakeExecutablePath());
        var resumed = await client.ResumeThreadAsync(started.ThreadId, temp.Path, null);

        Assert.Equal(started.ThreadId, resumed.ThreadId);
        Assert.Equal(started.ThreadId, client.CurrentThreadId);
        Assert.Equal(CodexConnectionState.Connected, client.State);
        logger.Dispose();
    }

    private static (CodexAppServerClient Client, CodexDiagnosticLogger Logger) CreateClient(string localData)
    {
        var logger = new CodexDiagnosticLogger(localData);
        var host = new CodexAppServerProcessHost();
        var transport = new CodexJsonRpcTransport(host, logger);
        return (new CodexAppServerClient(new CodexExecutableResolver(), transport, logger), logger);
    }

    private static string FakeExecutablePath()
    {
        var assemblyPath = typeof(FakeServerMarker).Assembly.Location;
        return Path.ChangeExtension(assemblyPath, ".exe");
    }
}
