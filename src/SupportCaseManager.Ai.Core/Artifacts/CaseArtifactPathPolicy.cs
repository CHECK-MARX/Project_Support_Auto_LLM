namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed class CaseArtifactPathPolicy
{
    public string NormalizeCaseFolder(string caseFolder)
    {
        if (string.IsNullOrWhiteSpace(caseFolder))
        {
            throw new InvalidOperationException("案件フォルダが未設定です。");
        }

        var fullPath = NormalizeDirectory(caseFolder);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"案件フォルダが見つかりません: {fullPath}");
        }

        return fullPath;
    }

    public string NormalizeSourceFile(string caseFolder, string sourceFilePath)
    {
        var root = NormalizeCaseFolder(caseFolder);
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new InvalidOperationException("元Excelファイルが未設定です。");
        }

        var fullPath = Path.GetFullPath(sourceFilePath);
        EnsureInside(root, fullPath, "元ファイル");
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("元Excelファイルが見つかりません。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("現在作成できる成果物は.xlsx形式だけです。");
        }

        EnsureNoEscapingDirectoryLink(root, Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    public string NormalizeDestinationFolder(string caseFolder, string destinationFolder)
    {
        var root = NormalizeCaseFolder(caseFolder);
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            throw new InvalidOperationException("保存先フォルダが未設定です。");
        }

        var fullPath = NormalizeDirectory(destinationFolder);
        EnsureInside(root, fullPath, "保存先");
        EnsureNoEscapingDirectoryLink(root, FindNearestExistingDirectory(fullPath));
        return fullPath;
    }

    public string BuildOutputPath(
        string caseFolder,
        string sourceFilePath,
        string destinationFolder,
        string outputFileName)
    {
        var root = NormalizeCaseFolder(caseFolder);
        var source = NormalizeSourceFile(root, sourceFilePath);
        var destination = NormalizeDestinationFolder(root, destinationFolder);
        ValidateOutputFileName(outputFileName);
        var output = Path.GetFullPath(Path.Combine(destination, outputFileName));
        EnsureInside(root, output, "出力ファイル");
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("元ファイルへの上書きは禁止されています。");
        }

        return output;
    }

    public string SuggestNumberedFileName(string destinationFolder, string outputFileName)
    {
        ValidateOutputFileName(outputFileName);
        var stem = Path.GetFileNameWithoutExtension(outputFileName);
        var extension = Path.GetExtension(outputFileName);
        for (var number = 2; number < 10_000; number++)
        {
            var candidate = $"{stem}_{number}{extension}";
            if (!File.Exists(Path.Combine(destinationFolder, candidate)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("連番付きの未使用ファイル名を作成できませんでした。");
    }

    public static void ValidateOutputFileName(string outputFileName)
    {
        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            throw new InvalidOperationException("出力ファイル名が未設定です。");
        }

        if (Path.IsPathRooted(outputFileName)
            || !string.Equals(Path.GetFileName(outputFileName), outputFileName, StringComparison.Ordinal)
            || outputFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("出力ファイル名にはフォルダや使用できない文字を含められません。");
        }

        if (!string.Equals(Path.GetExtension(outputFileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("出力ファイル名の拡張子は.xlsxにしてください。");
        }
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    private static void EnsureInside(string root, string candidate, string label)
    {
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"{label}は現在の案件フォルダ配下に限定されています: {candidate}");
        }
    }

    private static string FindNearestExistingDirectory(string path)
    {
        var current = path;
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException($"保存先の既存親フォルダを確認できません: {path}");
            }

            current = parent;
        }

        return current;
    }

    private static void EnsureNoEscapingDirectoryLink(string caseRoot, string existingDirectory)
    {
        var current = new DirectoryInfo(existingDirectory);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = current.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    throw new UnauthorizedAccessException($"リンク先を確認できないフォルダは成果物保存に使用できません: {current.FullName}");
                }

                EnsureInside(caseRoot, Path.GetFullPath(target.FullName), "リンク先");
            }

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    Path.TrimEndingDirectorySeparator(caseRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = current.Parent;
        }

        throw new UnauthorizedAccessException("保存先のCanonical Pathを案件フォルダ内として確認できませんでした。");
    }
}
