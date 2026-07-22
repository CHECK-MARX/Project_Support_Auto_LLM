namespace SupportCaseManager.Ai.Core.Codex;

public enum CodexCaseFileKind
{
    CustomerInquiry,
    Screenshot,
    Log,
    Configuration,
    SourceCode,
    Archive,
    Document,
    Other,
}

public sealed record CodexCaseFileInfo
{
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public CodexCaseFileKind Kind { get; init; }
    public long Size { get; init; }
    public DateTimeOffset LastModifiedAt { get; init; }
    public bool CanSendToCodex { get; init; }
    public bool IsImageInput { get; init; }
    public bool IsLarge { get; init; }
    public string ExclusionReason { get; init; } = string.Empty;
}

public sealed record CodexCaseFileScanResult(
    IReadOnlyList<CodexCaseFileInfo> Files,
    IReadOnlyList<string> Warnings);

public interface ICodexCaseFileScanner
{
    Task<CodexCaseFileScanResult> ScanAsync(string caseFolder, CancellationToken cancellationToken = default);
}

public sealed class CodexCaseFileScanner : ICodexCaseFileScanner
{
    public const long LargeFileThreshold = 20L * 1024 * 1024;
    public const long MaximumFileSize = 100L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".config",
        ".csv", ".tsv", ".md", ".markdown", ".rst", ".adoc", ".asciidoc", ".html", ".htm",
        ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".java", ".py", ".js", ".ts", ".sql", ".razor",
        ".ps1", ".bat", ".cmd", ".sh", ".properties", ".sln", ".csproj", ".vcxproj", ".gitignore",
        ".checkmarxignored",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz",
    };

    public Task<CodexCaseFileScanResult> ScanAsync(string caseFolder, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Scan(caseFolder, cancellationToken), cancellationToken);
    }

    private static CodexCaseFileScanResult Scan(string caseFolder, CancellationToken cancellationToken)
    {
        if (!CodexPathPolicy.TryNormalizeRoot(caseFolder, out var root, out var rootError))
        {
            return new CodexCaseFileScanResult([], [rootError]);
        }

        var files = new List<CodexCaseFileInfo>();
        var warnings = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pendingDirectories.Push(child);
                    }
                    else
                    {
                        warnings.Add($"リンク先フォルダは案件外参照防止のため除外しました: {Path.GetRelativePath(root, child)}");
                    }
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add($"リンクファイルは案件外参照防止のため除外しました: {Path.GetRelativePath(root, file)}");
                        continue;
                    }

                    if (!CodexPathPolicy.TryNormalizeFileWithinRoot(root, file, out var normalized, out var error))
                    {
                        warnings.Add($"{Path.GetFileName(file)}: {error}");
                        continue;
                    }

                    files.Add(CreateInfo(root, normalized));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                warnings.Add($"フォルダを読み取れません: {Path.GetRelativePath(root, directory)} ({ex.Message})");
            }
        }

        return new CodexCaseFileScanResult(
            files.OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings);
    }

    private static CodexCaseFileInfo CreateInfo(string root, string path)
    {
        var fileInfo = new FileInfo(path);
        var extension = fileInfo.Extension;
        var kind = Classify(fileInfo.Name, extension);
        var isImage = CodexPathPolicy.IsSupportedImage(path);
        var isArchive = ArchiveExtensions.Contains(extension);
        var isText = TextExtensions.Contains(extension);
        var isExtractableDocument = new[] { ".pdf", ".docx", ".xlsx", ".pptx" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
        var isReadableZip = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        var isLarge = fileInfo.Length >= LargeFileThreshold;
        var exclusion = string.Empty;
        var canUse = isImage || isText || isExtractableDocument || isReadableZip;
        if (isArchive && !isReadableZip)
        {
            canUse = false;
            exclusion = "ZIP以外の圧縮ファイルは自動展開しません。";
        }
        else if (!canUse)
        {
            exclusion = "Codexへ直接渡さないファイル形式です。";
        }
        else if (fileInfo.Length > MaximumFileSize)
        {
            canUse = false;
            exclusion = "100 MBを超えるため除外しました。";
        }
        else if (isLarge)
        {
            exclusion = "20 MB以上です。処理に時間がかかる可能性があります。";
        }

        return new CodexCaseFileInfo
        {
            FullPath = path,
            RelativePath = Path.GetRelativePath(root, path),
            FileName = fileInfo.Name,
            Kind = kind,
            Size = fileInfo.Length,
            LastModifiedAt = fileInfo.LastWriteTimeUtc,
            CanSendToCodex = canUse,
            IsImageInput = isImage,
            IsLarge = isLarge,
            ExclusionReason = exclusion,
        };
    }

    private static CodexCaseFileKind Classify(string fileName, string extension)
    {
        if (fileName.Contains("相談", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("問い合わせ", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("inquiry", StringComparison.OrdinalIgnoreCase))
        {
            return CodexCaseFileKind.CustomerInquiry;
        }

        if (CodexPathPolicy.IsSupportedImage(fileName))
        {
            return CodexCaseFileKind.Screenshot;
        }

        if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("log", StringComparison.OrdinalIgnoreCase))
        {
            return CodexCaseFileKind.Log;
        }

        if (new[] { ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".config", ".properties" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return CodexCaseFileKind.Configuration;
        }

        if (new[] { ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".java", ".py", ".js", ".ts", ".ps1", ".sh" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return CodexCaseFileKind.SourceCode;
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return CodexCaseFileKind.Archive;
        }

        if (new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return CodexCaseFileKind.Document;
        }

        return CodexCaseFileKind.Other;
    }
}
