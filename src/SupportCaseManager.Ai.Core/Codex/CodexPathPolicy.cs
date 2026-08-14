using SupportCaseManager.Ai.Core.IO;

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
            if (SafePathPolicy.HasAlternateDataStream(normalized)
                || !Directory.Exists(normalized)
                || SafePathPolicy.ContainsLinkedDirectory(normalized, normalized))
            {
                error = "案件フォルダが存在しません。";
                normalized = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
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
            if (SafePathPolicy.HasAlternateDataStream(candidate)
                || !File.Exists(candidate)
                || SafePathPolicy.IsLinkedFile(candidate))
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

            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrWhiteSpace(parent) || SafePathPolicy.ContainsLinkedDirectory(root, parent))
            {
                error = "選択したファイルのパスにリンクフォルダが含まれているため、Codexへ送信できません。";
                return false;
            }

            normalized = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
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
