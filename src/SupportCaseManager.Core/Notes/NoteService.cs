using System;
using System.Collections.Generic;
using System.IO;
using SupportCaseManager.Core.Compatibility;

namespace SupportCaseManager.Core.Notes;

public static class NoteService
{
    public static string EnsureNoteFile(string folderPath, NoteDefinition definition, string supportNumber)
    {
        Directory.CreateDirectory(folderPath);
        var path = ResolveNotePath(folderPath, definition, supportNumber);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty, EncodingPolicy.Utf8NoBom);
        }

        return path;
    }

    public static string AppendNote(string folderPath, NoteDefinition definition, string supportNumber, string status, string body)
    {
        var path = EnsureNoteFile(folderPath, definition, supportNumber);
        var timestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
        var header = $"*****追記部_{timestamp}({status})******";
        var footer = "--------------------------------------------------";
        var payload = string.Join(EncodingPolicy.LineEnding, header, body.Trim(), footer);
        AtomicAppend(path, payload);
        return path;
    }

    public static string CopyExistingText(string sourcePath, string folderPath, NoteDefinition definition, string supportNumber)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source note not found.", sourcePath);
        }

        Directory.CreateDirectory(folderPath);
        var target = Path.Combine(folderPath, definition.FileName(supportNumber));
        File.Copy(sourcePath, target, overwrite: true);

        var sourceInfo = new FileInfo(sourcePath);
        File.SetCreationTime(target, sourceInfo.CreationTime);
        File.SetLastWriteTime(target, sourceInfo.LastWriteTime);
        return target;
    }

    public static string CreateSubfolder(string folderPath, NoteDefinition definition, string supportNumber)
    {
        var baseName = definition.FolderName(supportNumber);
        var candidate = Path.Combine(folderPath, baseName);
        var counter = 1;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(folderPath, definition.FolderName(supportNumber, counter));
            counter += 1;
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    public static IEnumerable<string> NoteLabels()
    {
        foreach (var definition in NoteDefinitions.All)
        {
            yield return definition.Label;
        }
    }

    private static string ResolveNotePath(string folderPath, NoteDefinition definition, string supportNumber)
    {
        foreach (var candidate in definition.CandidateFileNames(supportNumber))
        {
            var path = Path.Combine(folderPath, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(folderPath, definition.FileName(supportNumber));
    }

    private static void AtomicAppend(string path, string text)
    {
        var original = ReadTextLossless(path);
        var trimmed = original.TrimEnd('\r', '\n');
        var newContent = string.IsNullOrEmpty(original)
            ? text
            : trimmed + EncodingPolicy.LineEnding + text;
        var temp = path + ".tmp";
        File.WriteAllText(temp, newContent, EncodingPolicy.Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    private static string ReadTextLossless(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var data = File.ReadAllBytes(path);
        return EncodingPolicy.DecodeNoteText(data);
    }
}
