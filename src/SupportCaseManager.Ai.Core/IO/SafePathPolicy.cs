namespace SupportCaseManager.Ai.Core.IO;

internal static class SafePathPolicy
{
    public static bool HasAlternateDataStream(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    public static bool IsLinkedFile(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
    }

    public static bool ContainsLinkedDirectory(string rootPath, string directoryPath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
            while (true)
            {
                var directory = new DirectoryInfo(current);
                if (directory.Exists && directory.LinkTarget is not null)
                {
                    return true;
                }

                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var parent = directory.Parent?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
    }
}
