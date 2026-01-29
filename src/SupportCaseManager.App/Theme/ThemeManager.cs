using System.Windows;
using System.Windows.Media;

namespace SupportCaseManager.App.Theme;

public static class ThemeManager
{
    private static ResourceDictionary? _light;
    private static ResourceDictionary? _dark;

    public static void Apply(System.Windows.Application app, bool dark)
    {
        _light ??= BuildLight();
        _dark ??= BuildDark();

        var dictionaries = app.Resources.MergedDictionaries;
        dictionaries.Remove(_light);
        dictionaries.Remove(_dark);
        dictionaries.Add(dark ? _dark : _light);
    }

    private static ResourceDictionary BuildLight()
    {
        var dict = new ResourceDictionary
        {
            ["AppBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 246, 248)),
            ["PanelBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            ["AppForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            ["InputBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            ["InputForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            ["BorderBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 215, 222)),
            ["ButtonBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),
            ["ButtonHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
            ["ButtonForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            ["ButtonBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
            ["DisabledForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            ["DisabledBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            ["StatusBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            ["StatusForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            ["ComboBoxItemBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            ["ComboBoxItemForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            ["ComboBoxItemHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 240, 255)),
            ["ComboBoxItemHoverForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            ["ComboBoxItemSelectedBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(191, 219, 254)),
            ["ComboBoxItemSelectedForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            ["ComboBoxDropDownBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            ["ComboBoxDropDownBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 215, 222)),
            ["ComboBoxButtonBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            ["ComboBoxButtonHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 240, 255)),
            ["ComboBoxButtonBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 215, 222)),
            ["ComboBoxButtonForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            ["MenuBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            ["MenuForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            ["MenuHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 240, 255)),
            ["MenuHoverForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            ["MenuBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 215, 222)),
            [System.Windows.SystemColors.WindowBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
            [System.Windows.SystemColors.WindowTextBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            [System.Windows.SystemColors.HighlightBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(191, 219, 254)),
            [System.Windows.SystemColors.HighlightTextBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
        };

        FreezeBrushes(dict);
        return dict;
    }

    private static ResourceDictionary BuildDark()
    {
        var dict = new ResourceDictionary
        {
            ["AppBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 22, 26)),
            ["PanelBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 31, 36)),
            ["AppForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["InputBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 36, 42)),
            ["InputForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["BorderBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 63, 69)),
            ["ButtonBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 167, 255)),
            ["ButtonHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 142, 228)),
            ["ButtonForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 16, 26)),
            ["ButtonBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 142, 228)),
            ["DisabledForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(118, 127, 138)),
            ["DisabledBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 47, 54)),
            ["StatusBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 31, 36)),
            ["StatusForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 216, 223)),
            ["ComboBoxItemBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 36, 42)),
            ["ComboBoxItemForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["ComboBoxItemHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 52, 60)),
            ["ComboBoxItemHoverForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["ComboBoxItemSelectedBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(64, 74, 90)),
            ["ComboBoxItemSelectedForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["ComboBoxDropDownBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 36, 42)),
            ["ComboBoxDropDownBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 63, 69)),
            ["ComboBoxButtonBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 36, 42)),
            ["ComboBoxButtonHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 52, 60)),
            ["ComboBoxButtonBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 63, 69)),
            ["ComboBoxButtonForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["MenuBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 36, 42)),
            ["MenuForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["MenuHoverBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 52, 60)),
            ["MenuHoverForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            ["MenuBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 63, 69)),
            [System.Windows.SystemColors.WindowBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 36, 42)),
            [System.Windows.SystemColors.WindowTextBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
            [System.Windows.SystemColors.HighlightBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(64, 74, 90)),
            [System.Windows.SystemColors.HighlightTextBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)),
        };

        FreezeBrushes(dict);
        return dict;
    }

    private static void FreezeBrushes(ResourceDictionary dict)
    {
        foreach (var value in dict.Values)
        {
            if (value is SolidColorBrush brush && brush.CanFreeze)
            {
                brush.Freeze();
            }
        }
    }
}
