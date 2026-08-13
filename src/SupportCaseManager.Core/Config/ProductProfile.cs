using System.Collections.Generic;

namespace SupportCaseManager.Core.Config;

public sealed class ProductProfile : ProductDefinition
{
    public List<Dictionary<string, string>> NoteTemplates { get; set; } = new();

    // Legacy property names are kept for source and JSON migration compatibility.
    public string Name
    {
        get => DisplayName;
        set => DisplayName = value;
    }

    public string BasePath
    {
        get => BaseFolder;
        set => BaseFolder = value;
    }

    public string ClosedPath
    {
        get => ClosedFolder;
        set => ClosedFolder = value;
    }
}
