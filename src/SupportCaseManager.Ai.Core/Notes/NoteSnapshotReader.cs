using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.IO;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Ai.Core.Notes;

public sealed class NoteSnapshotReader : INoteSnapshotReader
{
    private static readonly EnumerationOptions NoteEnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public async Task<IReadOnlyList<NoteSnapshot>> ReadAllAsync(
        string caseFolderPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseFolderPath) || !Directory.Exists(caseFolderPath))
        {
            return [];
        }

        string normalizedCaseFolder;
        try
        {
            normalizedCaseFolder = Path.GetFullPath(caseFolderPath);
            if (SafePathPolicy.HasAlternateDataStream(normalizedCaseFolder)
                || SafePathPolicy.ContainsLinkedDirectory(normalizedCaseFolder, normalizedCaseFolder))
            {
                return [];
            }
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(caseFolderPath))
        {
            return [];
        }

        var notes = new List<NoteSnapshot>();
        foreach (var path in Directory.EnumerateFiles(normalizedCaseFolder, "*.txt", NoteEnumerationOptions)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafePathPolicy.TryNormalizeDescendant(normalizedCaseFolder, path, out var normalizedPath))
            {
                continue;
            }

            var snapshot = await ReadCoreAsync(normalizedPath, cancellationToken);
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
        if (!TryResolveReadableFile(rootPath: null, noteFilePath, out var normalizedPath))
        {
            return null;
        }

        return await ReadCoreAsync(normalizedPath, cancellationToken);
    }

    private static async Task<NoteSnapshot?> ReadCoreAsync(
        string noteFilePath,
        CancellationToken cancellationToken)
    {
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

    private static bool TryResolveReadableFile(string? rootPath, string? filePath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(filePath);
            if (SafePathPolicy.HasAlternateDataStream(normalizedPath)
                || !File.Exists(normalizedPath)
                || SafePathPolicy.IsLinkedFile(normalizedPath))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return true;
            }

            var normalizedRoot = Path.GetFullPath(rootPath);
            var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
            var isInsideRoot = !Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
            return isInsideRoot
                && Path.GetDirectoryName(normalizedPath) is { } parent
                && !SafePathPolicy.ContainsLinkedDirectory(normalizedRoot, parent);
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(filePath))
        {
            normalizedPath = string.Empty;
            return false;
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
