using System.Collections.Generic;

namespace SupportCaseManager.Core.Config;

public sealed class ProductProfile
{
    public string Name { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
    public string ClosedPath { get; set; } = string.Empty;
    public List<Dictionary<string, string>> NoteTemplates { get; set; } = new();
}
