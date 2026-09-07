using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using SupportCaseManager.App;
using SupportCaseManager.App.ViewModels;
using SupportCaseManager.Core.Config;
using SupportCaseManager.Core.Logging;
using SupportCaseManager.Core.Repository;
using SupportCaseManager.App.Tests.Helpers;

namespace SupportCaseManager.App.Tests;

public sealed class MainWindowLifecycleTests
{
    [Fact]
    public void OnClosing_AllowsNormalCloseWithoutHidingMainWindow()
    {
        var result = RunOnSta(() =>
        {
            using var temp = new TempDirectory();
            var app = new App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            app.InitializeComponent();
            var viewModel = CreateViewModel(temp.Path);
            var window = new MainWindow(viewModel);
            app.MainWindow = window;
            window.Visibility = Visibility.Visible;
            var args = new CancelEventArgs();

            var onClosing = window.GetType().GetMethod(
                "OnClosing",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(MainWindow).FullName, "OnClosing");
            onClosing.Invoke(window, [args]);

            return (args.Cancel, window.Visibility);
        });

        Assert.False(result.Cancel);
        Assert.Equal(Visibility.Visible, result.Visibility);
    }

    private static MainViewModel CreateViewModel(string configDirectory)
    {
        return new MainViewModel(
            new ConfigStore(configDirectory),
            new CaseRepository(NullLogger.Instance),
            NullLogger.Instance);
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

}
