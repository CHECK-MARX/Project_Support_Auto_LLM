namespace SupportCaseManager.Core.Cases;

public static class CaseFolderPathPolicy
{
    public static bool TryNormalizeConfiguredRoot(
        string? rootPath,
        bool createIfMissing,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (!TryNormalizePath(rootPath, requireExisting: false, out var candidate) ||
            HasAlternateDataStream(candidate))
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

            normalizedPath = Path.TrimEndingDirectorySeparator(directory.FullName);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    public static bool TryNormalizeExistingFolderWithinRoots(
        string? folderPath,
        IEnumerable<string?> allowedRoots,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (!TryNormalizeDirectory(folderPath, out var candidate) || HasAlternateDataStream(candidate))
        {
            return false;
        }

        foreach (var configuredRoot in allowedRoots)
        {
            if (!TryNormalizeDirectory(configuredRoot, out var root) ||
                !IsStrictDescendant(root, candidate) ||
                ContainsLinkedDirectory(root, candidate))
            {
                continue;
            }

            normalizedPath = candidate;
            return true;
        }

        return false;
    }

    public static bool TryNormalizeDestinationWithinRoots(
        string? destinationPath,
        IEnumerable<string?> allowedRoots,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (!TryNormalizePath(destinationPath, requireExisting: false, out var candidate) ||
            HasAlternateDataStream(candidate))
        {
            return false;
        }

        foreach (var configuredRoot in allowedRoots)
        {
            if (!TryNormalizeDirectory(configuredRoot, out var root) ||
                !IsStrictDescendant(root, candidate) ||
                ContainsLinkedDirectory(root, candidate))
            {
                continue;
            }

            normalizedPath = candidate;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeDirectory(string? path, out string normalized)
    {
        return TryNormalizePath(path, requireExisting: true, out normalized);
    }

    private static bool TryNormalizePath(string? path, bool requireExisting, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return !requireExisting || Directory.Exists(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        try
        {
            var relative = Path.GetRelativePath(root, candidate);
            if (string.IsNullOrWhiteSpace(relative) ||
                string.Equals(relative, ".", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                return false;
            }

            return !string.Equals(relative, "..", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ContainsLinkedDirectory(string root, string candidate)
    {
        var current = candidate;
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var directory = new DirectoryInfo(current);
                if (directory.Exists && directory.LinkTarget is not null)
                {
                    return true;
                }

                var parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAlternateDataStream(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }
}
