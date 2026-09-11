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
        var fullPath = NormalizeSelectedSourceFile(caseFolder, sourceFilePath);
        if (GetArtifactFormat(fullPath) == ArtifactFormat.Unsupported)
        {
            throw new InvalidOperationException("このファイル形式は現在、翻訳保存に対応していません。");
        }

        return fullPath;
    }

    public string NormalizeSelectedSourceFile(string caseFolder, string sourceFilePath)
    {
        var root = NormalizeCaseFolder(caseFolder);
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new InvalidOperationException("翻訳元ファイルが未設定です。");
        }

        var fullPath = Path.GetFullPath(sourceFilePath.Trim());
        EnsureInside(root, fullPath, "元ファイル");
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("翻訳元ファイルが見つかりません。", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                throw new UnauthorizedAccessException($"リンク先を確認できない元ファイルは使用できません: {fullPath}");
            }

            EnsureInside(root, Path.GetFullPath(target.FullName), "元ファイルのリンク先");
        }

        EnsureNoEscapingDirectoryLink(root, Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    public static ArtifactFormat GetArtifactFormat(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".xlsx" => ArtifactFormat.ExcelWorkbook,
            ".csv" => ArtifactFormat.Csv,
            ".txt" => ArtifactFormat.PlainText,
            ".md" => ArtifactFormat.Markdown,
            _ => ArtifactFormat.Unsupported,
        };
    }

    public static bool IsSupportedSource(string filePath) => GetArtifactFormat(filePath) != ArtifactFormat.Unsupported;

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
        if (!string.Equals(
                Path.GetExtension(source),
                Path.GetExtension(output),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("出力ファイルの拡張子は翻訳元ファイルと同じにしてください。");
        }

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

    public static string SuggestDefaultFileName(string sourceFilePath)
    {
        return new ArtifactFilenameTranslationService().CreatePreview(sourceFilePath).OutputFileName;
    }

    public string SuggestDateFileName(
        string destinationFolder,
        string sourceFilePath,
        DateTime localDate)
    {
        return SuggestDateFileNameFromOutput(
            destinationFolder,
            SuggestDefaultFileName(sourceFilePath),
            localDate);
    }

    public string SuggestDateFileNameFromOutput(
        string destinationFolder,
        string outputFileName,
        DateTime localDate)
    {
        ValidateOutputFileName(outputFileName);
        var extension = Path.GetExtension(outputFileName);
        var stem = Path.GetFileNameWithoutExtension(outputFileName);
        var date = localDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var baseName = stem.EndsWith("_EN", StringComparison.OrdinalIgnoreCase)
            ? $"{stem}_{date}{extension}"
            : $"{stem}_EN_{date}{extension}";
        ValidateOutputFileName(baseName, GetArtifactFormat(outputFileName));
        if (!File.Exists(Path.Combine(destinationFolder, baseName)))
        {
            return baseName;
        }

        return SuggestNumberedFileName(destinationFolder, baseName);
    }

    public static void ValidateOutputFileName(
        string outputFileName,
        ArtifactFormat expectedFormat = ArtifactFormat.Unsupported)
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

        var actualFormat = GetArtifactFormat(outputFileName);
        if (actualFormat == ArtifactFormat.Unsupported)
        {
            throw new InvalidOperationException("このファイル形式は現在、翻訳保存に対応していません。");
        }

        if (expectedFormat != ArtifactFormat.Unsupported && actualFormat != expectedFormat)
        {
            throw new InvalidOperationException("出力ファイルの拡張子は翻訳元ファイルと同じにしてください。");
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
