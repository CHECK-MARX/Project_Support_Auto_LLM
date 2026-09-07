using System.Diagnostics;

namespace SupportCaseManager.AiAssistant.App.Shutdown;

internal sealed record BoundedShutdownResult(
    bool SettingsCompleted,
    bool CodexShutdownCompleted,
    bool CodexDisposeCompleted,
    bool CleanupCompleted,
    bool TimedOut,
    bool HadException);

internal static class BoundedShutdownExecutor
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<BoundedShutdownResult> ExecuteAsync(
        Func<Task> saveSettings,
        Func<Task>? shutdownCodex,
        Func<Task>? disposeCodex,
        Func<Task> cleanup,
        TimeSpan timeout,
        Action<string, Exception?> report)
    {
        ArgumentNullException.ThrowIfNull(saveSettings);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(report);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var stopwatch = Stopwatch.StartNew();
        var timedOut = false;
        var hadException = false;

        var settingsCompleted = await RunStepAsync(
            "Settings save",
            saveSettings,
            stopwatch,
            timeout,
            allowAfterDeadline: false,
            report,
            exception => hadException = true,
            () => timedOut = true).ConfigureAwait(true);

        var codexShutdownCompleted = shutdownCodex is null || await RunStepAsync(
            "Codex graceful shutdown",
            shutdownCodex,
            stopwatch,
            timeout,
            allowAfterDeadline: false,
            report,
            exception => hadException = true,
            () => timedOut = true).ConfigureAwait(true);

        var codexDisposeCompleted = disposeCodex is null || await RunStepAsync(
            "Codex dispose",
            disposeCodex,
            stopwatch,
            timeout,
            allowAfterDeadline: false,
            report,
            exception => hadException = true,
            () => timedOut = true).ConfigureAwait(true);

        var cleanupCompleted = await RunStepAsync(
            "Evidence selector cleanup",
            cleanup,
            stopwatch,
            timeout,
            allowAfterDeadline: true,
            report,
            exception => hadException = true,
            () => timedOut = true).ConfigureAwait(true);

        return new BoundedShutdownResult(
            settingsCompleted,
            codexShutdownCompleted,
            codexDisposeCompleted,
            cleanupCompleted,
            timedOut,
            hadException);
    }

    private static async Task<bool> RunStepAsync(
        string name,
        Func<Task> action,
        Stopwatch stopwatch,
        TimeSpan timeout,
        bool allowAfterDeadline,
        Action<string, Exception?> report,
        Action<Exception> markException,
        Action markTimeout)
    {
        var remaining = timeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero && !allowAfterDeadline)
        {
            markTimeout();
            report($"{name} skipped because shutdown deadline was reached.", null);
            return false;
        }

        Task task;
        try
        {
            task = action() ?? throw new InvalidOperationException($"{name} returned a null task.");
        }
        catch (Exception exception)
        {
            markException(exception);
            report($"{name} failed.", exception);
            return false;
        }

        if (remaining <= TimeSpan.Zero)
        {
            markTimeout();
            report($"{name} started after the shutdown deadline; final close will continue.", null);
            _ = ObserveCompletionAsync(name, task, report);
            return false;
        }

        try
        {
            await task.WaitAsync(remaining).ConfigureAwait(true);
            return true;
        }
        catch (TimeoutException)
        {
            markTimeout();
            report($"{name} timed out after the shutdown deadline.", null);
            _ = ObserveCompletionAsync(name, task, report);
            return false;
        }
        catch (Exception exception)
        {
            markException(exception);
            report($"{name} failed.", exception);
            return false;
        }
    }

    private static async Task ObserveCompletionAsync(
        string name,
        Task task,
        Action<string, Exception?> report)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            report($"{name} failed after shutdown continued.", exception);
        }
    }
}
