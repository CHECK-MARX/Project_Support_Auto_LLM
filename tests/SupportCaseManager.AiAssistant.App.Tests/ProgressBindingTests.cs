using System.Xml.Linq;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class ProgressBindingTests
{
    [Fact]
    public void MainWindow_ProgressBarUsesOneWayBindingForReadOnlyProgress()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var progressBar = FindProgressBar(document, "OperationProgressPercent");
        var value = Assert.Single(
            progressBar.Attributes(),
            attribute => attribute.Name.LocalName == "Value").Value;

        Assert.Contains("OperationProgressPercent", value, StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", value, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CodexProgressAndEditableDraftsAreBound()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var codexProgress = FindProgressBar(document, "Codex.ProgressPercent");
        var progressValue = Assert.Single(
            codexProgress.Attributes(),
            attribute => attribute.Name.LocalName == "Value").Value;
        Assert.Contains("Codex.ProgressPercent", progressValue, StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", progressValue, StringComparison.Ordinal);

        AssertEditableTextBox(document, "CustomerReplyDraft");
        AssertEditableTextBox(document, "InternalMemo");

        foreach (var bindingName in new[] { "Codex.Version", "Codex.Model", "Codex.DiagnosticsPath" })
        {
            var bindings = document.Descendants()
                .SelectMany(static element => element.Attributes())
                .Where(attribute => attribute.Value.Contains(bindingName, StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(bindings);
            Assert.All(bindings, binding => Assert.Contains("Mode=OneWay", binding.Value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MainWindow_CodexMessageReadOnlyHeadersUseOneWayBindings()
    {
        var document = XDocument.Load(FindMainWindowPath());

        foreach (var bindingName in new[] { "RoleDisplay", "CreatedAtText" })
        {
            var runAttributes = document.Descendants()
                .Where(element => element.Name.LocalName == "Run")
                .SelectMany(static element => element.Attributes())
                .ToList();
            var binding = Assert.Single(runAttributes, attribute =>
                attribute.Name.LocalName == "Text" &&
                attribute.Value.Contains(bindingName, StringComparison.Ordinal));

            Assert.Contains("Mode=OneWay", binding.Value, StringComparison.Ordinal);
        }
    }

    private static XElement FindProgressBar(XDocument document, string bindingText)
    {
        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ProgressBar" &&
                       element.Attributes().Any(attribute =>
                           attribute.Name.LocalName == "Value" &&
                           attribute.Value.Contains(bindingText, StringComparison.Ordinal)));
    }

    private static void AssertEditableTextBox(XDocument document, string bindingText)
    {
        var textBox = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "TextBox" &&
                       element.Attributes().Any(attribute =>
                           attribute.Name.LocalName == "Text" &&
                           attribute.Value.Contains(bindingText, StringComparison.Ordinal)));
        Assert.Equal("False", textBox.Attribute("IsReadOnly")?.Value);
        Assert.Contains("Mode=TwoWay", textBox.Attribute("Text")!.Value, StringComparison.Ordinal);
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
