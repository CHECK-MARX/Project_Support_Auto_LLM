using System.Xml.Linq;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class DarkThemeTests
{
    [Theory]
    [InlineData("Theme.Dark.xaml")]
    [InlineData("Theme.Light.xaml")]
    public void SelectionAndHoverStyles_UseReadableForegroundAndBackground(string fileName)
    {
        var document = XDocument.Load(FindThemePath(fileName));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var styles = document.Descendants(presentation + "Style").ToList();

        AssertReadableState(styles, "TabItem", "IsSelected");
        AssertReadableState(styles, "TabItem", "IsMouseOver");
        AssertReadableState(styles, "ComboBoxItem", "IsSelected");
        AssertReadableState(styles, "ComboBoxItem", "IsMouseOver");
        AssertReadableState(styles, "DataGridRow", "IsSelected");
    }

    [Fact]
    public void DarkTheme_UsesDarkNormalComboBoxAndBlueSelection()
    {
        var document = XDocument.Load(FindThemePath("Theme.Dark.xaml"));
        var resources = document.Root!.Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                element => element.Attributes().First(attribute => attribute.Name.LocalName == "Key").Value,
                element => element.Attribute("Color")!.Value,
                StringComparer.Ordinal);

        Assert.NotEqual(resources["AppControlBackgroundBrush"], resources["AppForegroundBrush"]);
        Assert.NotEqual(resources["AppSelectionBrush"], resources["AppSelectionForegroundBrush"]);
        Assert.StartsWith("#1F", resources["AppSelectionBrush"], StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertReadableState(
        IReadOnlyList<XElement> styles,
        string targetType,
        string triggerProperty)
    {
        var style = Assert.Single(styles, element =>
            element.Attribute("TargetType")?.Value.Contains(targetType, StringComparison.Ordinal) == true);
        var trigger = Assert.Single(style.Descendants(), element =>
            element.Name.LocalName == "Trigger" &&
            string.Equals(element.Attribute("Property")?.Value, triggerProperty, StringComparison.Ordinal));
        var setters = trigger.Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(
                element => element.Attribute("Property")!.Value,
                element => element.Attribute("Value")!.Value,
                StringComparer.Ordinal);

        Assert.True(setters.ContainsKey("Background"));
        Assert.True(setters.ContainsKey("Foreground"));
        Assert.NotEqual(setters["Background"], setters["Foreground"]);
    }

    private static string FindThemePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "Resources",
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Theme file was not found: {fileName}");
    }
}
