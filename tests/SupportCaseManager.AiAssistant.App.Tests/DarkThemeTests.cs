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
        AssertReadableState(styles, "Button", "IsEnabled", "False");
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

    [Theory]
    [InlineData("TabItem", "TabBorder", "TabHeader", "IsSelected")]
    [InlineData("ComboBox", "ComboBorder", "SelectedContent", "IsDropDownOpen")]
    [InlineData("ComboBoxItem", "ItemBorder", "ItemContent", "IsSelected")]
    [InlineData("Button", "ButtonBorder", "ButtonContent", "IsEnabled")]
    public void DarkTheme_CustomTemplatesPaintVisibleBackgroundAndForeground(
        string targetType,
        string backgroundTarget,
        string foregroundTarget,
        string stateProperty)
    {
        var document = XDocument.Load(FindThemePath("Theme.Dark.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var style = Assert.Single(
            document.Descendants(presentation + "Style"),
            element => TargetsType(element, targetType));
        var templateSetter = Assert.Single(
            style.Elements(presentation + "Setter"),
            element => string.Equals(element.Attribute("Property")?.Value, "Template", StringComparison.Ordinal));
        var template = Assert.Single(
            templateSetter.Descendants(presentation + "ControlTemplate"),
            element => TargetsType(element, targetType));
        var backgroundElement = FindNamedElement(template, backgroundTarget);
        var foregroundElement = FindNamedElement(template, foregroundTarget);

        Assert.Contains("TemplateBinding Background", AttributeValue(backgroundElement, "Background"), StringComparison.Ordinal);
        Assert.Contains("TemplateBinding Foreground", AttributeValue(foregroundElement, "Foreground"), StringComparison.Ordinal);

        var stateTrigger = Assert.Single(
            template.Descendants(presentation + "Trigger"),
            element => string.Equals(element.Attribute("Property")?.Value, stateProperty, StringComparison.Ordinal));
        Assert.Contains(stateTrigger.Elements(presentation + "Setter"), element =>
            string.Equals(element.Attribute("TargetName")?.Value, backgroundTarget, StringComparison.Ordinal) &&
            string.Equals(element.Attribute("Property")?.Value, "Background", StringComparison.Ordinal));
        Assert.Contains(stateTrigger.Elements(presentation + "Setter"), element =>
            string.Equals(element.Attribute("TargetName")?.Value, foregroundTarget, StringComparison.Ordinal) &&
            element.Attribute("Property")?.Value.EndsWith("Foreground", StringComparison.Ordinal) == true);
    }

    private static void AssertReadableState(
        IReadOnlyList<XElement> styles,
        string targetType,
        string triggerProperty,
        string triggerValue = "True")
    {
        var style = Assert.Single(styles, element => TargetsType(element, targetType));
        var trigger = Assert.Single(style.Elements()
            .Where(element => element.Name.LocalName == "Style.Triggers")
            .SelectMany(static element => element.Elements()), element =>
            element.Name.LocalName == "Trigger" &&
            string.Equals(element.Attribute("Property")?.Value, triggerProperty, StringComparison.Ordinal) &&
            string.Equals(element.Attribute("Value")?.Value, triggerValue, StringComparison.Ordinal));
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

    private static bool TargetsType(XElement style, string targetType)
    {
        return style.Attribute("TargetType")?.Value.EndsWith($"{targetType}}}", StringComparison.Ordinal) == true;
    }

    private static XElement FindNamedElement(XElement root, string name)
    {
        return Assert.Single(root.Descendants(), element => element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && string.Equals(attribute.Value, name, StringComparison.Ordinal)));
    }

    private static string AttributeValue(XElement element, string propertyName)
    {
        return Assert.Single(element.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(propertyName, StringComparison.Ordinal)).Value;
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
