namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class MainWindowShutdownContractTests
{
    [Fact]
    public void ShutdownIsBoundedAndDoesNotUseProcessAbort()
    {
        var source = File.ReadAllText(FindMainWindowSource());

        Assert.Contains("BoundedShutdownExecutor.ExecuteAsync", source);
        Assert.Contains("BoundedShutdownExecutor.DefaultTimeout", source);
        Assert.Contains("e.Cancel = true", source);
        Assert.Contains("shutdownStarted", source);
        Assert.Contains("shutdownComplete", source);
        Assert.Contains("allowClose", source);
        Assert.Contains("Dispatcher.InvokeAsync(Close", source);
        Assert.DoesNotContain("Environment.Exit", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSaveIsStartedBeforeCodexShutdown()
    {
        var source = File.ReadAllText(FindMainWindowSource());

        Assert.True(
            source.IndexOf("ViewModel.FlushSettingsAsync", StringComparison.Ordinal)
                < source.IndexOf("codex.ShutdownAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void CloseGatePrecedesCancellationAndFinalCloseIsScheduledOnce()
    {
        var source = File.ReadAllText(FindMainWindowSource());

        Assert.True(
            source.IndexOf("if (allowClose || shutdownComplete)", StringComparison.Ordinal)
                < source.IndexOf("e.Cancel = true", StringComparison.Ordinal));
        Assert.Equal(1, Count(source, "BoundedShutdownExecutor.ExecuteAsync"));
        Assert.Equal(1, Count(source, "Dispatcher.InvokeAsync(Close"));
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindMainWindowSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "MainWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("MainWindow.xaml.cs was not found.");
    }
}
