using System.Windows;
using WpfApplication = System.Windows.Application;

namespace SupportCaseManager.AiAssistant.App.Appearance;

public sealed class AppAppearanceService : IAppAppearanceService
{
    private const string JapaneseLanguage = "ja-JP";
    private const string EnglishLanguage = "en-US";

    public void Apply(string? uiLanguage, bool useDarkMode)
    {
        var application = WpfApplication.Current;
        if (application is null)
        {
            return;
        }

        RemoveAppearanceDictionaries(application.Resources.MergedDictionaries);

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{NormalizeLanguage(uiLanguage)}.xaml", UriKind.Relative),
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Theme.{(useDarkMode ? "Dark" : "Light")}.xaml", UriKind.Relative),
        });
    }

    private static void RemoveAppearanceDictionaries(IList<ResourceDictionary> dictionaries)
    {
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            var source = dictionaries[index].Source?.OriginalString ?? string.Empty;
            if (source.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase)
                || source.Contains("Resources/Theme.", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(index);
            }
        }
    }

    private static string NormalizeLanguage(string? uiLanguage)
    {
        return string.Equals(uiLanguage, EnglishLanguage, StringComparison.OrdinalIgnoreCase)
            ? EnglishLanguage
            : JapaneseLanguage;
    }
}
