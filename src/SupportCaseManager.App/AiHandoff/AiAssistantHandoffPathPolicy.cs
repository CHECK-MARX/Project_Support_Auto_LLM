using System;
using System.IO;

namespace SupportCaseManager.App.AiHandoff;

internal static class AiAssistantHandoffPathPolicy
{
    private const string FileNamePrefix = "ai-context-";

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "itoke",
        "SupportCaseManager",
        "ai-handoff");

    public static bool TryNormalizeRoot(string? folderPath, bool createIfMissing, out string normalizedRoot)
    {
        normalizedRoot = string.Empty;
        if (!TryGetFullPath(folderPath, out var candidate) || HasAlternateDataStream(candidate))
        {
            return false;
        }

        try
        {
            if (createIfMissing)
            {
                Directory.CreateDirectory(candidate);
            }

            var directory = new DirectoryInfo(candidate);
            if (!directory.Exists || directory.LinkTarget is not null)
            {
                return false;
            }

            normalizedRoot = Path.TrimEndingDirectorySeparator(directory.FullName);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryNormalizeContextFile(
        string? contextFilePath,
        string root,
        bool requireExisting,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (!TryNormalizeRoot(root, createIfMissing: false, out var normalizedRoot) ||
            !TryGetFullPath(contextFilePath, out var candidate) ||
            HasAlternateDataStream(candidate))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(candidate);
        var fileName = Path.GetFileName(candidate);
        if (!string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            !fileName.StartsWith(FileNamePrefix, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(candidate);
            if (requireExisting && !file.Exists)
            {
                return false;
            }

            if (file.Exists && file.LinkTarget is not null)
            {
                return false;
            }

            normalizedPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetFullPath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasAlternateDataStream(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }
}
