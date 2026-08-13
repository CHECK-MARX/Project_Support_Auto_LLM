using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexRealSmokeTests
{
    [Fact]
    public async Task RealCodex_InitializeThreadAndReceiveAnswer_WhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SUPPORT_CASE_MANAGER_RUN_CODEX_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var temp = new TempDirectory();
        using var logger = new CodexDiagnosticLogger(temp.Path);
        var transport = new CodexJsonRpcTransport(new CodexAppServerProcessHost(), logger);
        await using var client = new CodexAppServerClient(new CodexExecutableResolver(), transport, logger);
        var completion = new TaskCompletionSource<CodexTurnCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = string.Empty;
        client.AgentMessageDelta += (_, args) => answer += args.Delta;
        client.TurnCompleted += (_, args) => completion.TrySetResult(args);
        var executable = Environment.GetEnvironmentVariable("SUPPORT_CASE_MANAGER_CODEX_PATH")
            ?? @"C:\Users\itoke\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe";

        var connection = await client.ConnectAsync(executable);
        var thread = await client.StartThreadAsync(temp.Path, null);
        await client.StartTurnAsync("接続試験です。ファイルやネットワークを使わず、CODEX_SMOKE_OK の1行だけを返してください。");
        var completed = await completion.Task.WaitAsync(TimeSpan.FromMinutes(3));

        Assert.True(connection.Account.IsChatGptAuthenticated);
        Assert.False(string.IsNullOrWhiteSpace(thread.ThreadId));
        Assert.Equal("completed", completed.Status);
        Assert.Contains("CODEX_SMOKE_OK", answer, StringComparison.OrdinalIgnoreCase);
    }
}
