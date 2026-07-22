using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App;

public partial class MainWindow : Window
{
    private bool shutdownStarted;
    private bool shutdownComplete;

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
        if (shutdownComplete)
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
            if (ViewModel.Codex is not null)
            {
                await ViewModel.Codex.ShutdownAsync();
                await ViewModel.Codex.DisposeAsync();
            }

            await ViewModel.FlushSettingsAsync();
        }
        catch
        {
            // Auto-save already covers normal changes; shutdown must not be blocked by an I/O failure.
        }
        finally
        {
            shutdownComplete = true;
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.ApplicationIdle);
        }
    }
}
