using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SupportCaseManager.AiAssistant.App.Shutdown;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App;

public partial class MainWindow : Window
{
    private bool shutdownStarted;
    private bool shutdownComplete;
    private bool allowClose;

    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose || shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (shutdownStarted)
        {
            return;
        }

        shutdownStarted = true;
        try
        {
            var codex = ViewModel.Codex;
            var result = await BoundedShutdownExecutor.ExecuteAsync(
                ViewModel.FlushSettingsAsync,
                codex is null ? null : codex.ShutdownAsync,
                codex is null ? null : () => codex.DisposeAsync().AsTask(),
                () => Task.Run(ViewModel.ShutdownEvidenceSelector),
                BoundedShutdownExecutor.DefaultTimeout,
                ReportShutdownIssue);

            if (result.TimedOut || result.HadException || !result.SettingsCompleted)
            {
                ReportShutdownIssue(
                    $"Shutdown completed with warnings. SettingsCompleted={result.SettingsCompleted}; TimedOut={result.TimedOut}; HadException={result.HadException}.",
                    null);
            }
        }
        catch (Exception exception)
        {
            ReportShutdownIssue("Shutdown coordinator failed unexpectedly.", exception);
        }
        finally
        {
            shutdownComplete = true;
            allowClose = true;
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.ApplicationIdle);
        }
    }

    private static void ReportShutdownIssue(string message, Exception? exception)
    {
        var detail = exception is null
            ? message
            : $"{message} {exception.GetType().Name}: {exception.Message}";

        if (exception is null)
        {
            Trace.TraceWarning(detail);
        }
        else
        {
            Trace.TraceError(detail);
        }
    }
}
