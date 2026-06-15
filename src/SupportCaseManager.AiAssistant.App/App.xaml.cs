using SupportCaseManager.AiAssistant.App.Composition;
using SupportCaseManager.AiAssistant.App.Launch;

namespace SupportCaseManager.AiAssistant.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = AppCompositionRoot.CreateMainWindow();
        mainWindow.Show();

        var options = new CommandLineArgsParser().Parse(e.Args);
        await mainWindow.ViewModel.InitializeFromCommandLineAsync(options);
    }
}
