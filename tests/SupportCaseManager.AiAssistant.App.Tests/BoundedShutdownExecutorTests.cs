using System.Diagnostics;
using SupportCaseManager.AiAssistant.App.Shutdown;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class BoundedShutdownExecutorTests
{
    [Fact]
    public async Task ExecutesSettingsBeforeCodexAndCleanupOnce()
    {
        var order = new List<string>();

        var result = await BoundedShutdownExecutor.ExecuteAsync(
            () => RecordAsync(order, "settings"),
            () => RecordAsync(order, "shutdown"),
            () => RecordAsync(order, "dispose"),
            () => RecordAsync(order, "cleanup"),
            TimeSpan.FromSeconds(1),
            (_, _) => { });

        Assert.True(result.SettingsCompleted);
        Assert.True(result.CodexShutdownCompleted);
        Assert.True(result.CodexDisposeCompleted);
        Assert.True(result.CleanupCompleted);
        Assert.False(result.TimedOut);
        Assert.False(result.HadException);
        Assert.Equal(["settings", "shutdown", "dispose", "cleanup"], order);
    }

    [Fact]
    public async Task TimesOutStalledCodexAndStillStartsCleanup()
    {
        var cleanupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<string>();

        var stopwatch = Stopwatch.StartNew();
        var result = await BoundedShutdownExecutor.ExecuteAsync(
            () => Task.CompletedTask,
            () => Task.Delay(Timeout.InfiniteTimeSpan),
            () => throw new Xunit.Sdk.XunitException("Dispose must not race a stalled shutdown."),
            () =>
            {
                cleanupStarted.SetResult(true);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(50),
            (message, _) => diagnostics.Add(message));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(result.TimedOut);
        Assert.False(result.CodexShutdownCompleted);
        Assert.False(result.CodexDisposeCompleted);
        Assert.False(result.CleanupCompleted);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains(diagnostics, message => message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecordsExceptionAndContinuesToDisposeAndCleanup()
    {
        var order = new List<string>();
        var diagnostics = new List<string>();

        var result = await BoundedShutdownExecutor.ExecuteAsync(
            () => RecordAsync(order, "settings"),
            () =>
            {
                order.Add("shutdown");
                throw new InvalidOperationException("disconnect failed");
            },
            () => RecordAsync(order, "dispose"),
            () => RecordAsync(order, "cleanup"),
            TimeSpan.FromSeconds(1),
            (message, _) => diagnostics.Add(message));

        Assert.True(result.SettingsCompleted);
        Assert.False(result.CodexShutdownCompleted);
        Assert.True(result.CodexDisposeCompleted);
        Assert.True(result.CleanupCompleted);
        Assert.False(result.TimedOut);
        Assert.True(result.HadException);
        Assert.Equal(["settings", "shutdown", "dispose", "cleanup"], order);
        Assert.Contains(diagnostics, message => message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SettingsExceptionDoesNotPreventCleanup()
    {
        var order = new List<string>();

        var result = await BoundedShutdownExecutor.ExecuteAsync(
            () =>
            {
                order.Add("settings");
                throw new IOException("settings unavailable");
            },
            () => RecordAsync(order, "shutdown"),
            () => RecordAsync(order, "dispose"),
            () => RecordAsync(order, "cleanup"),
            TimeSpan.FromSeconds(1),
            (_, _) => { });

        Assert.False(result.SettingsCompleted);
        Assert.True(result.CodexShutdownCompleted);
        Assert.True(result.CodexDisposeCompleted);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.HadException);
        Assert.Equal(["settings", "shutdown", "dispose", "cleanup"], order);
    }

    private static Task RecordAsync(List<string> order, string name)
    {
        order.Add(name);
        return Task.CompletedTask;
    }
}
