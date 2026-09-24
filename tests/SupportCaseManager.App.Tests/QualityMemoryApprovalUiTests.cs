using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace SupportCaseManager.App.Tests;

public sealed class QualityMemoryApprovalUiTests
{
    [Fact]
    public void NoteEditorHasExplicitQualityApprovalButton()
    {
        var path = FindMainWindowPath();
        var buttons = XDocument.Load(path).Descendants().Where(element => element.Name.LocalName == "Button");
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "品質改善に登録"
            && button.Attribute("Click")?.Value == "OnQualityMemoryApprove");
    }

    private static string FindMainWindowPath([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", "src",
            "SupportCaseManager.App", "MainWindow.xaml"));
}
