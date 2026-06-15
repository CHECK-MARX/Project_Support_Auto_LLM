using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Ai.Core.Notes;

public sealed class NoteSnapshotReader : INoteSnapshotReader
{
    public async Task<IReadOnlyList<NoteSnapshot>> ReadAllAsync(
        string caseFolderPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseFolderPath) || !Directory.Exists(caseFolderPath))
        {
            return [];
        }

        var notes = new List<NoteSnapshot>();
        foreach (var path in Directory.EnumerateFiles(caseFolderPath, "*.txt").OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await ReadAsync(path, cancellationToken);
            if (snapshot is not null)
            {
                notes.Add(snapshot);
            }
        }

        return notes;
    }

    public async Task<NoteSnapshot?> ReadAsync(
        string noteFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(noteFilePath) || !File.Exists(noteFilePath))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(noteFilePath, cancellationToken);
            var text = EncodingPolicy.DecodeNoteText(bytes);
            var info = new FileInfo(noteFilePath);

            return new NoteSnapshot
            {
                NoteKind = DetectNoteKind(info.Name),
                FilePath = info.FullName,
                FileName = info.Name,
                Text = text,
                LastModifiedAt = info.LastWriteTime,
                IsCurrent = false,
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string DetectNoteKind(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
        {
            return "Unknown";
        }

        foreach (var definition in NoteDefinitions.All)
        {
            if (MatchesNoteBaseName(nameWithoutExtension, definition.BaseName))
            {
                return definition.Label;
            }

            foreach (var legacyBaseName in definition.LegacyBaseNames)
            {
                if (MatchesNoteBaseName(nameWithoutExtension, legacyBaseName))
                {
                    return definition.Label;
                }
            }
        }

        return "Unknown";
    }

    private static bool MatchesNoteBaseName(string nameWithoutExtension, string baseName)
    {
        return string.Equals(nameWithoutExtension, baseName, StringComparison.Ordinal)
            || nameWithoutExtension.StartsWith($"{baseName}_", StringComparison.Ordinal);
    }
}
