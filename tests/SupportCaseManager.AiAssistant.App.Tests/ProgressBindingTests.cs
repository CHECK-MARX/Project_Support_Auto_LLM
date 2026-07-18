using System.Xml.Linq;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class ProgressBindingTests
{
    [Fact]
    public void MainWindow_ProgressBarUsesOneWayBindingForReadOnlyProgress()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var progressBar = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ProgressBar");
        var value = Assert.Single(
            progressBar.Attributes(),
            attribute => attribute.Name.LocalName == "Value").Value;

        Assert.Contains("OperationProgressPercent", value, StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", value, StringComparison.Ordinal);
    }

    private static string FindMainWindowPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("MainWindow.xaml was not found.");
    }
}
