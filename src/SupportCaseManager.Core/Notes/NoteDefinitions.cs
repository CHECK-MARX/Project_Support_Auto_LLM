using System.Collections.Generic;

namespace SupportCaseManager.Core.Notes;

public static class NoteDefinitions
{
    public static IReadOnlyList<NoteDefinition> All { get; } = new List<NoteDefinition>
    {
        new(
            "consult",
            "お客様ご相談内容",
            "お客様ご相談内容",
            "お客様ご相談内容"),
        new(
            "reply",
            "お客様への返信案",
            "お客様への返信案",
            "返信案"),
        new(
            "vendor",
            "メーカー連携内容",
            "メーカー連携内容",
            "メーカー連携内容"),
    };

    public static NoteDefinition GetByKey(string? key)
    {
        foreach (var definition in All)
        {
            if (definition.Key == key)
            {
                return definition;
            }
        }

        return All[0];
    }
}
