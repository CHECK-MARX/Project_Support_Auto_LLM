using System;
using System.Collections.Generic;

namespace SupportCaseManager.Core.Notes;

public sealed class NoteDefinition
{
    public NoteDefinition(
        string key,
        string label,
        string baseName,
        string folderPrefix,
        IReadOnlyList<string>? legacyBaseNames = null)
    {
        Key = key;
        Label = label;
        BaseName = baseName;
        FolderPrefix = folderPrefix;
        LegacyBaseNames = legacyBaseNames ?? Array.Empty<string>();
    }

    public string Key { get; }
    public string Label { get; }
    public string BaseName { get; }
    public string FolderPrefix { get; }
    public IReadOnlyList<string> LegacyBaseNames { get; }

    public string FileName(string supportNumber)
    {
        var formatted = Cases.CaseNaming.SanitizeComponent(Cases.CaseNaming.FormatSupportNumber(supportNumber));
        var suffix = string.IsNullOrEmpty(formatted) ? string.Empty : $"_{formatted}";
        return $"{BaseName}{suffix}.txt";
    }

    public IEnumerable<string> CandidateFileNames(string supportNumber)
    {
        var formatted = Cases.CaseNaming.SanitizeComponent(Cases.CaseNaming.FormatSupportNumber(supportNumber));
        var suffix = string.IsNullOrEmpty(formatted) ? string.Empty : $"_{formatted}";
        yield return $"{BaseName}{suffix}.txt";
        foreach (var legacy in LegacyBaseNames)
        {
            if (string.IsNullOrWhiteSpace(legacy))
            {
                continue;
            }

            yield return $"{legacy}{suffix}.txt";
        }
    }

    public string FolderName(string supportNumber, int counter = 0, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).ToString("yyyyMMdd");
        var formatted = Cases.CaseNaming.SanitizeComponent(Cases.CaseNaming.FormatSupportNumber(supportNumber));
        var suffix = string.IsNullOrEmpty(formatted) ? string.Empty : $"_{formatted}";
        var baseName = $"{FolderPrefix}_{today}{suffix}";
        return counter > 0 ? $"{baseName}_{counter}" : baseName;
    }
}
