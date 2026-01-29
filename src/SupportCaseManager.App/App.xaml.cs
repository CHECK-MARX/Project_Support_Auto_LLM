using System;
using System.IO;
using System.Windows;
using SupportCaseManager.App.Theme;
using SupportCaseManager.App.ViewModels;
using SupportCaseManager.Core.Config;
using SupportCaseManager.Core.Logging;
using SupportCaseManager.Core.Repository;

namespace SupportCaseManager.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logger = CreateLogger();
        RegisterGlobalHandlers(logger);

        try
        {
            var config = new ConfigStore();
            var repository = new CaseRepository(logger);
            var viewModel = new MainViewModel(config, repository, logger);

            ThemeManager.Apply(Current, viewModel.Settings.DarkMode);

            var window = new MainWindow(viewModel);
            window.Show();
            InitializeTrayIcon();
        }
        catch (Exception ex)
        {
            logger.Error("Startup failed", ex);
            System.Windows.MessageBox.Show($"起動に失敗しました。\n{ex.Message}", "起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }

    private static IAppLogger CreateLogger()
    {
        var primaryPath = Path.Combine(AppContext.BaseDirectory, "logs", "SupportCaseManager.log");
        try
        {
            return new FileLogger(primaryPath);
        }
        catch
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var fallback = Path.Combine(local, "itoke", "SupportCaseManager", "logs", "SupportCaseManager.log");
                return new FileLogger(fallback);
            }
            catch
            {
                return NullLogger.Instance;
            }
        }
    }

    private void RegisterGlobalHandlers(IAppLogger logger)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("Unhandled UI exception", args.Exception);
            System.Windows.MessageBox.Show($"エラーが発生しました。\n{args.Exception.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                logger.Error("Unhandled exception", ex);
            }
            else
            {
                logger.Error($"Unhandled exception: {args.ExceptionObject}");
            }
        };
    }

    private void InitializeTrayIcon()
    {
        if (_trayIcon != null)
        {
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("表示", null, (_, _) => ShowMainWindow());
        menu.Items.Add("終了", null, (_, _) => Shutdown());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "サポート受付ディレクトリ作成ツール",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                return new System.Drawing.Icon(iconPath);
            }
        }
        catch
        {
            // fallback below
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void ShowMainWindow()
    {
        if (Current?.MainWindow is not Window window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}
