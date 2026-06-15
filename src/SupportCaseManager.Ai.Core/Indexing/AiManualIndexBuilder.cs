using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed class AiManualIndexBuilder : IAiManualIndexBuilder
{
    public const string IndexFileName = "manuals-index.json";

    private const int ChunkMaxLength = 2600;
    private const int ChunkOverlapLength = 150;

    private static readonly Regex MarkdownHeadingRegex = new("^(?<hash>#{1,3})\\s+(?<title>.+?)\\s*$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Func<DateTimeOffset> nowProvider;

    public AiManualIndexBuilder(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public async Task<AiManualIndexBuildResult> BuildAsync(
        string manualFolder,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        return await BuildManyAsync([manualFolder], aiIndexFolder, cancellationToken);
    }

    public async Task<AiManualIndexBuildResult> BuildManyAsync(
        IReadOnlyList<string> manualFolders,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder))
        {
            throw new ArgumentException("AI index folder is required.", nameof(aiIndexFolder));
        }

        manualFolders ??= [];
        Directory.CreateDirectory(aiIndexFolder);
        var indexFilePath = Path.Combine(aiIndexFolder, IndexFileName);
        var warnings = new List<string>();
        var indexedManuals = new List<AiIndexedManual>();
        var seenFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsupportedExtensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unsupportedDocumentExtensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outOfScopeExtensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scannedFileCount = 0;
        var supportedFileCount = 0;
        var unsupportedFileCount = 0;
        var contentExcludedFileCount = 0;
        var unsupportedDocumentFileCount = 0;
        var outOfScopeFileCount = 0;
        var otherUnsupportedFileCount = 0;
        var emptyFileSkippedCount = 0;
        var readFailureCount = 0;
        var duplicateFileSkippedCount = 0;
        var indexedFileCount = 0;
        var errorCount = 0;

        var targetFolders = manualFolders
            .Where(static folder => !string.IsNullOrWhiteSpace(folder))
            .Select(static folder => folder.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetFolders.Count == 0)
        {
            warnings.Add("Manual folder does not exist.");
            errorCount += 1;
            await WriteIndexAsync(indexFilePath, string.Empty, indexedManuals, cancellationToken);
            return new AiManualIndexBuildResult
            {
                IndexedFileCount = 0,
                IndexedChunkCount = 0,
                ErrorCount = errorCount,
                IndexFilePath = indexFilePath,
                UnsupportedExtensionCounts = unsupportedExtensionCounts,
                Warnings = warnings,
            };
        }

        foreach (var manualFolder in targetFolders)
        {
            if (!Directory.Exists(manualFolder))
            {
                errorCount += 1;
                warnings.Add($"Manual folder does not exist: {manualFolder}");
                continue;
            }

            foreach (var filePath in EnumerateFilesSafely(manualFolder, warnings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedFileCount += 1;

                if (!seenFilePaths.Add(filePath))
                {
                    duplicateFileSkippedCount += 1;
                    continue;
                }

                var fileClassification = ManualDocumentFilter.ClassifyFile(filePath);
                if (fileClassification.Category != ManualDocumentCategory.ImportCandidate)
                {
                    unsupportedFileCount += 1;
                    Increment(unsupportedExtensionCounts, fileClassification.Extension);
                    switch (fileClassification.Category)
                    {
                        case ManualDocumentCategory.UnsupportedDocumentFormat:
                            unsupportedDocumentFileCount += 1;
                            Increment(unsupportedDocumentExtensionCounts, fileClassification.Extension);
                            break;
                        case ManualDocumentCategory.OutOfScopeBinaryOrArchive:
                            outOfScopeFileCount += 1;
                            Increment(outOfScopeExtensionCounts, fileClassification.Extension);
                            break;
                        default:
                            otherUnsupportedFileCount += 1;
                            break;
                    }

                    continue;
                }

                supportedFileCount += 1;

                try
                {
                    var text = await ReadUtf8StrictAsync(filePath, cancellationToken);
                    var contentClassification = ManualDocumentFilter.ClassifyTextFileContent(filePath, text);
                    if (contentClassification.Category == ManualDocumentCategory.ContentExcludedText)
                    {
                        contentExcludedFileCount += 1;
                        warnings.Add($"Skipped non-manual text file: {filePath}. {contentClassification.Reason}");
                        continue;
                    }

                    var fileInfo = new FileInfo(filePath);
                    var chunks = CreateManualChunks(fileInfo, text).ToList();
                    if (chunks.Count == 0)
                    {
                        emptyFileSkippedCount += 1;
                        warnings.Add($"Skipped empty manual file: {filePath}");
                        continue;
                    }

                    indexedFileCount += 1;
                    indexedManuals.AddRange(chunks);
                }
                catch (DecoderFallbackException)
                {
                    readFailureCount += 1;
                    errorCount += 1;
                    warnings.Add($"Skipped manual file because it is not valid UTF-8: {filePath}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    readFailureCount += 1;
                    errorCount += 1;
                    warnings.Add($"Failed to index manual file: {filePath}. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        await WriteIndexAsync(indexFilePath, string.Join(Path.PathSeparator, targetFolders), indexedManuals, cancellationToken);
        return new AiManualIndexBuildResult
        {
            ScannedFileCount = scannedFileCount,
            SupportedFileCount = supportedFileCount,
            UnsupportedFileCount = unsupportedFileCount,
            ContentExcludedFileCount = contentExcludedFileCount,
            UnsupportedDocumentFileCount = unsupportedDocumentFileCount,
            OutOfScopeFileCount = outOfScopeFileCount,
            OtherUnsupportedFileCount = otherUnsupportedFileCount,
            EmptyFileSkippedCount = emptyFileSkippedCount,
            ReadFailureCount = readFailureCount,
            DuplicateFileSkippedCount = duplicateFileSkippedCount,
            IndexedFileCount = indexedFileCount,
            IndexedChunkCount = indexedManuals.Count,
            ErrorCount = errorCount,
            IndexFilePath = indexFilePath,
            UnsupportedExtensionCounts = unsupportedExtensionCounts
                .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase),
            UnsupportedDocumentExtensionCounts = unsupportedDocumentExtensionCounts
                .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase),
            OutOfScopeExtensionCounts = outOfScopeExtensionCounts
                .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase),
            Warnings = warnings,
        };
    }

    private async Task WriteIndexAsync(
        string indexFilePath,
        string sourceFolder,
        IReadOnlyList<AiIndexedManual> manuals,
        CancellationToken cancellationToken)
    {
        var document = new AiManualIndexDocument
        {
            BuiltAt = nowProvider(),
            SourceFolder = sourceFolder,
            Manuals = manuals,
        };

        await using var stream = File.Create(indexFilePath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string manualFolder, List<string> warnings)
    {
        var pending = new Stack<string>();
        pending.Push(manualFolder);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Failed to enumerate manual files in folder: {directory}. {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory).OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Failed to enumerate manual subfolders in folder: {directory}. {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                pending.Push(subdirectory);
            }
        }
    }

    private static void Increment(IDictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var count)
            ? count + 1
            : 1;
    }

    private static async Task<string> ReadUtf8StrictAsync(string filePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF'
            ? text[1..]
            : text;
    }

    private static IEnumerable<AiIndexedManual> CreateManualChunks(FileInfo fileInfo, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var sections = IsMarkdown(fileInfo)
            ? SplitMarkdownSections(text).ToList()
            : [new ManualSection(string.Empty, text)];

        var chunkIndex = 0;
        foreach (var section in sections)
        {
            foreach (var chunk in SplitIntoChunks(section.Text))
            {
                if (string.IsNullOrWhiteSpace(chunk))
                {
                    continue;
                }

                yield return new AiIndexedManual
                {
                    Id = BuildId(fileInfo.FullName, chunkIndex, section.SectionTitle),
                    FilePath = fileInfo.FullName,
                    FileName = fileInfo.Name,
                    Title = BuildTitle(fileInfo, section.SectionTitle),
                    DocumentType = IsMarkdown(fileInfo) ? "Markdown" : "Text",
                    SectionTitle = section.SectionTitle,
                    Text = chunk,
                    LastModifiedAt = fileInfo.LastWriteTime,
                };
                chunkIndex += 1;
            }
        }
    }

    private static bool IsMarkdown(FileInfo fileInfo)
    {
        return string.Equals(fileInfo.Extension, ".md", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ManualSection> SplitMarkdownSections(string text)
    {
        var currentTitle = string.Empty;
        var builder = new StringBuilder();
        var hasHeading = false;

        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var match = MarkdownHeadingRegex.Match(line);
            if (match.Success)
            {
                if (builder.Length > 0)
                {
                    yield return new ManualSection(currentTitle, builder.ToString());
                    builder.Clear();
                }

                currentTitle = match.Groups["title"].Value.Trim();
                hasHeading = true;
            }

            builder.AppendLine(line);
        }

        if (builder.Length > 0)
        {
            yield return new ManualSection(currentTitle, builder.ToString());
        }
        else if (!hasHeading && !string.IsNullOrWhiteSpace(text))
        {
            yield return new ManualSection(string.Empty, text);
        }
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(ChunkMaxLength, text.Length - start);
            var chunk = text.Substring(start, length);
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                yield return chunk;
            }

            if (start + length >= text.Length)
            {
                break;
            }

            start += Math.Max(1, ChunkMaxLength - ChunkOverlapLength);
        }
    }

    private static string BuildTitle(FileInfo fileInfo, string sectionTitle)
    {
        var fileTitle = Path.GetFileNameWithoutExtension(fileInfo.Name);
        return string.IsNullOrWhiteSpace(sectionTitle)
            ? fileTitle
            : $"{fileTitle} - {sectionTitle}";
    }

    private static string BuildId(string filePath, int chunkIndex, string sectionTitle)
    {
        var raw = $"{filePath}|{chunkIndex}|{sectionTitle}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private sealed record ManualSection(string SectionTitle, string Text);
}
