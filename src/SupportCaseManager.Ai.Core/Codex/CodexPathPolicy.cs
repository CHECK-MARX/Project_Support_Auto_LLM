namespace SupportCaseManager.Ai.Core.Codex;

public static class CodexPathPolicy
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
    };

    public static bool TryNormalizeRoot(string? path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "案件フォルダが指定されていません。";
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(normalized))
            {
                error = "案件フォルダが存在しません。";
                normalized = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "案件フォルダのパスが正しくありません。";
            return false;
        }
    }

    public static bool TryNormalizeFileWithinRoot(
        string rootPath,
        string? filePath,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (!TryNormalizeRoot(rootPath, out var root, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "ファイルが指定されていません。";
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(filePath);
            if (!File.Exists(candidate))
            {
                error = "選択したファイルが存在しません。";
                return false;
            }

            var relative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                error = "案件フォルダ外のファイルはCodexへ渡せません。";
                return false;
            }

            normalized = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "ファイルのパスが正しくありません。";
            return false;
        }
    }

    public static bool IsSupportedImage(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path));
    }
}
