using SupportCaseManager.Core.Config;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Config;

public class ConfigStoreTests
{
    [Fact]
    public void SaveAndLoad_PreservesSettings()
    {
        using var temp = new TempDirectory();
        var configDir = System.IO.Path.Combine(temp.Path, "config");
        var store = new ConfigStore(configDir);

        var settings = new UserSettings
        {
            BasePath = "C:\\Cases",
            DarkMode = false,
            WindowGeometry = new List<int> { 1, 2, 3 },
            SplitterState = new List<int>(),
            RecentCases = new List<string> { "C:\\Cases\\A" },
            Statuses = new List<string> { "受付", "調査中" },
            NoteTemplates = new List<Dictionary<string, string>>
            {
                new() { ["name"] = "テンプレート", ["text"] = "内容" }
            }
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(settings.BasePath, loaded.BasePath);
        Assert.Equal(settings.DarkMode, loaded.DarkMode);
        Assert.Equal(settings.WindowGeometry, loaded.WindowGeometry);
        Assert.Equal(settings.RecentCases, loaded.RecentCases);
        Assert.Equal(settings.Statuses, loaded.Statuses);
        Assert.Equal(settings.NoteTemplates.Count, loaded.NoteTemplates.Count);
    }
}
