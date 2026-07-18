using System.Windows;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) =>
        {
            try
            {
                Task.Run(viewModel.FlushSettingsAsync).GetAwaiter().GetResult();
            }
            catch
            {
                // Auto-save already covers normal changes; shutdown must not be blocked by an I/O failure.
            }
        };
    }
}
