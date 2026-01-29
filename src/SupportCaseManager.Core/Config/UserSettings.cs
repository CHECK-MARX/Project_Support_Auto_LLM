using System.Collections.Generic;

namespace SupportCaseManager.Core.Config;

public sealed class UserSettings
{
    public string BasePath { get; set; } = string.Empty;
    public bool DarkMode { get; set; } = true;
    public List<int> WindowGeometry { get; set; } = new();
    public List<int> SplitterState { get; set; } = new();
    public List<string> RecentCases { get; set; } = new();
    public List<string> Statuses { get; set; } = new(Defaults.DefaultStatuses);
    public List<Dictionary<string, string>> NoteTemplates { get; set; } = new();

    public UserSettings Update(string? basePath = null, bool? darkMode = null)
    {
        if (basePath != null)
        {
            BasePath = basePath;
        }

        if (darkMode.HasValue)
        {
            DarkMode = darkMode.Value;
        }

        return this;
    }
}
