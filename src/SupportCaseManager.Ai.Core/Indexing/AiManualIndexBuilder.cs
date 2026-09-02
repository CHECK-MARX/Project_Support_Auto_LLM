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
    private readonly SafeZipManualReader safeZipReader;

    public AiManualIndexBuilder(
        Func<DateTimeOffset>? nowProvider = null,
        SafeZipManualReader? safeZipReader = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        this.safeZipReader = safeZipReader ?? new SafeZipManualReader();
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
        var provenanceEntries = new List<SourceRegistryEntry>();
        var parsedArtifacts = new List<ParsedSourceArtifact>();
        var seenFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regularCandidates = new List<string>();
        var archiveCandidates = new List<string>();
        var diagnostics = new List<string>();
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
        var commandHeavyManualIncludedCount = 0;
        var archivesScannedCount = 0;
        var zipFileCount = 0;
        var zipEntryCount = 0;
        var supportedZipEntryCount = 0;
        var indexedZipEntryCount = 0;
        var skippedZipEntryCount = 0;
        var duplicateZipEntryCount = 0;
        var encryptedZipCount = 0;
        var corruptZipCount = 0;
        var unsafeArchivePathRejectedCount = 0;
        var archiveSizeLimitExceededCount = 0;
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
                if (fileClassification.Category == ManualDocumentCategory.ImportCandidate)
                {
                    supportedFileCount += 1;
                    regularCandidates.Add(filePath);
                    continue;
                }

                if (fileClassification.Category == ManualDocumentCategory.ArchiveCandidate)
                {
                    supportedFileCount += 1;
                    archiveCandidates.Add(filePath);
                    continue;
                }

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
            }
        }

        foreach (var filePath in regularCandidates.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contentHash = await ComputeFileSha256Async(filePath, cancellationToken);
                var content = await ManualDocumentTextExtractor.ReadAsync(filePath, cancellationToken);
                var contentClassification = ManualDocumentFilter.ClassifyTextFileContent(filePath, content.Text);
                AddContentDiagnostic(diagnostics, filePath, contentClassification);
                if (contentClassification.Category == ManualDocumentCategory.ContentExcludedText)
                {
                    contentExcludedFileCount += 1;
                    warnings.Add($"Skipped non-manual text file: {filePath}. {contentClassification.Reason}");
                    continue;
                }

                var source = ManualSourceDescriptor.ForFile(filePath, contentHash, FindCorpusRoot(filePath, targetFolders));
                var chunks = CreateManualChunks(source, content).ToList();
                if (chunks.Count == 0)
                {
                    emptyFileSkippedCount += 1;
                    warnings.Add($"Skipped empty manual file: {filePath}");
                    continue;
                }

                if (!seenContentHashes.Add(contentHash))
                {
                    duplicateFileSkippedCount += 1;
                    diagnostics.Add($"Duplicate skipped: {filePath}; sha256={contentHash}");
                    continue;
                }

                if ((contentClassification.Scores?.CommandExampleScore ?? 0) >= 2)
                {
                    commandHeavyManualIncludedCount += 1;
                }

                indexedFileCount += 1;
                indexedManuals.AddRange(chunks);
                AddProvenance(source, content, GetProductNameHint(aiIndexFolder), provenanceEntries, parsedArtifacts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                readFailureCount += 1;
                errorCount += 1;
                warnings.Add($"Failed to index manual file: {filePath}. {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var archivePath in archiveCandidates.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            archivesScannedCount += 1;
            zipFileCount += 1;
            SafeZipManualReadResult archiveResult;
            try
            {
                archiveResult = await safeZipReader.ReadAsync(archivePath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                readFailureCount += 1;
                errorCount += 1;
                warnings.Add($"ZIP could not be indexed and was skipped: {archivePath}. {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            zipEntryCount += archiveResult.ZipEntryCount;
            supportedZipEntryCount += archiveResult.SupportedEntryCount;
            skippedZipEntryCount += archiveResult.SkippedEntryCount;
            encryptedZipCount += archiveResult.EncryptedArchiveCount;
            corruptZipCount += archiveResult.CorruptArchiveCount;
            unsafeArchivePathRejectedCount += archiveResult.UnsafePathRejectedCount;
            archiveSizeLimitExceededCount += archiveResult.SizeLimitExceededCount;
            warnings.AddRange(archiveResult.Warnings);

            foreach (var entry in archiveResult.Entries)
            {
                var sourcePath = BuildArchiveSourcePath(entry.ArchivePath, entry.EntryPath);
                var contentClassification = ManualDocumentFilter.ClassifyTextFileContent(sourcePath, entry.Content.Text);
                AddContentDiagnostic(diagnostics, sourcePath, contentClassification);
                if (contentClassification.Category == ManualDocumentCategory.ContentExcludedText)
                {
                    contentExcludedFileCount += 1;
                    skippedZipEntryCount += 1;
                    warnings.Add($"Skipped non-manual ZIP entry: {sourcePath}. {contentClassification.Reason}");
                    continue;
                }

                var source = ManualSourceDescriptor.ForArchiveEntry(entry, sourcePath, FindCorpusRoot(entry.ArchivePath, targetFolders));
                var chunks = CreateManualChunks(source, entry.Content).ToList();
                if (chunks.Count == 0)
                {
                    emptyFileSkippedCount += 1;
                    skippedZipEntryCount += 1;
                    warnings.Add($"Skipped empty manual ZIP entry: {sourcePath}");
                    continue;
                }

                if (!seenContentHashes.Add(entry.Sha256))
                {
                    duplicateFileSkippedCount += 1;
                    duplicateZipEntryCount += 1;
                    skippedZipEntryCount += 1;
                    diagnostics.Add($"Duplicate skipped: {sourcePath}; sha256={entry.Sha256}");
                    continue;
                }

                if ((contentClassification.Scores?.CommandExampleScore ?? 0) >= 2)
                {
                    commandHeavyManualIncludedCount += 1;
                }

                indexedFileCount += 1;
                indexedZipEntryCount += 1;
                indexedManuals.AddRange(chunks);
                AddProvenance(source, entry.Content, GetProductNameHint(aiIndexFolder), provenanceEntries, parsedArtifacts);
            }
        }

        await WriteIndexAsync(indexFilePath, string.Join(Path.PathSeparator, targetFolders), indexedManuals, cancellationToken);
        await ProvenanceRegistryStore.SaveAsync(aiIndexFolder, provenanceEntries, parsedArtifacts, cancellationToken);
        var pageNumberChunkCount = indexedManuals.Count(static item => item.PageNumber is > 0);
        var sectionTitleChunkCount = indexedManuals.Count(static item => !string.IsNullOrWhiteSpace(item.SectionTitle));
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
            CommandHeavyManualIncludedCount = commandHeavyManualIncludedCount,
            ArchivesScannedCount = archivesScannedCount,
            ZipFileCount = zipFileCount,
            ZipEntryCount = zipEntryCount,
            SupportedZipEntryCount = supportedZipEntryCount,
            IndexedZipEntryCount = indexedZipEntryCount,
            SkippedZipEntryCount = skippedZipEntryCount,
            DuplicateZipEntryCount = duplicateZipEntryCount,
            EncryptedZipCount = encryptedZipCount,
            CorruptZipCount = corruptZipCount,
            UnsafeArchivePathRejectedCount = unsafeArchivePathRejectedCount,
            ArchiveSizeLimitExceededCount = archiveSizeLimitExceededCount,
            IndexedFileCount = indexedFileCount,
            IndexedChunkCount = indexedManuals.Count,
            PageNumberChunkCount = pageNumberChunkCount,
            SectionTitleChunkCount = sectionTitleChunkCount,
            PageAndSectionChunkCount = indexedManuals.Count(static item =>
                item.PageNumber is > 0 && !string.IsNullOrWhiteSpace(item.SectionTitle)),
            ZipDerivedChunkCount = indexedManuals.Count(static item => !string.IsNullOrWhiteSpace(item.ArchivePath)),
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
            Diagnostics = diagnostics,
        };
    }

    public async Task<AiManualIndexBuildResult> BuildManyIncrementalAsync(
        IReadOnlyList<string> manualFolders,
        string aiIndexFolder,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder))
        {
            throw new ArgumentException("AI index folder is required.", nameof(aiIndexFolder));
        }

        Directory.CreateDirectory(aiIndexFolder);
        var indexFilePath = Path.Combine(aiIndexFolder, IndexFileName);
        var targetFolders = (manualFolders ?? [])
            .Where(static folder => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            .Select(static folder => Path.GetFullPath(folder.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var warnings = new List<string>();
        var allCurrentFiles = targetFolders
            .SelectMany(folder => EnumerateFilesSafely(folder, warnings))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allCurrentFiles.Any(path => ManualDocumentFilter.ClassifyFile(path).Category == ManualDocumentCategory.ArchiveCandidate))
        {
            return await BuildManyAsync(targetFolders, aiIndexFolder, cancellationToken);
        }

        var existing = await ReadExistingIndexAsync(indexFilePath, cancellationToken);
        var existingByPath = existing.Manuals
            .Where(static item => item.ArchivePath is null && !string.IsNullOrWhiteSpace(item.FilePath))
            .GroupBy(static item => Path.GetFullPath(item.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var currentFiles = allCurrentFiles
            .Where(path => ManualDocumentFilter.ClassifyFile(path).Category == ManualDocumentCategory.ImportCandidate)
            .ToList();
        var currentPaths = currentFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new List<AiIndexedManual>();
        var provenanceEntries = new List<SourceRegistryEntry>();
        var parsedArtifacts = new List<ParsedSourceArtifact>();
        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var errors = 0;

        foreach (var filePath in currentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(filePath);
            existingByPath.TryGetValue(filePath, out var oldChunks);
            var isUnchanged = !forceRebuild && oldChunks is { Count: > 0 } &&
                oldChunks.All(chunk => chunk.LastModifiedAt == fileInfo.LastWriteTime);
            if (isUnchanged)
            {
                output.AddRange(oldChunks!);
                unchanged += 1;
                continue;
            }

            try
            {
                var contentHash = await ComputeFileSha256Async(filePath, cancellationToken);
                var content = await ManualDocumentTextExtractor.ReadAsync(filePath, cancellationToken);
                var classification = ManualDocumentFilter.ClassifyTextFileContent(filePath, content.Text);
                if (classification.Category == ManualDocumentCategory.ContentExcludedText)
                {
                    continue;
                }

                var source = ManualSourceDescriptor.ForFile(filePath, contentHash, FindCorpusRoot(filePath, targetFolders));
                var chunks = CreateManualChunks(source, content).ToList();
                output.AddRange(chunks);
                AddProvenance(source, content, GetProductNameHint(aiIndexFolder), provenanceEntries, parsedArtifacts);
                if (oldChunks is { Count: > 0 })
                {
                    changed += 1;
                }
                else
                {
                    added += 1;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors += 1;
                warnings.Add($"Failed to update manual file: {filePath}. {ex.GetType().Name}: {ex.Message}");
                if (oldChunks is { Count: > 0 })
                {
                    output.AddRange(oldChunks);
                }
            }
        }

        var deleted = existingByPath.Keys.Count(path => !currentPaths.Contains(path));
        await WriteIndexAtomicallyAsync(
            indexFilePath,
            string.Join(Path.PathSeparator, targetFolders),
            output,
            cancellationToken);
        await ProvenanceRegistryStore.SaveAsync(aiIndexFolder, provenanceEntries, parsedArtifacts, cancellationToken);

        return new AiManualIndexBuildResult
        {
            ScannedFileCount = currentFiles.Count,
            SupportedFileCount = currentFiles.Count,
            IndexedFileCount = output.Select(static item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            IndexedChunkCount = output.Count,
            PageNumberChunkCount = output.Count(static item => item.PageNumber is > 0),
            SectionTitleChunkCount = output.Count(static item => !string.IsNullOrWhiteSpace(item.SectionTitle)),
            PageAndSectionChunkCount = output.Count(static item =>
                item.PageNumber is > 0 && !string.IsNullOrWhiteSpace(item.SectionTitle)),
            ZipDerivedChunkCount = output.Count(static item => !string.IsNullOrWhiteSpace(item.ArchivePath)),
            AddedFileCount = added,
            ChangedFileCount = changed,
            DeletedFileCount = deleted,
            UnchangedFileCount = unchanged,
            ErrorCount = errors,
            IndexFilePath = indexFilePath,
            Warnings = warnings,
        };
    }

    private async Task WriteIndexAsync(
        string indexFilePath,
        string sourceFolder,
        IReadOnlyList<AiIndexedManual> manuals,
        CancellationToken cancellationToken)
    {
        await WriteIndexAtomicallyAsync(indexFilePath, sourceFolder, manuals, cancellationToken);
    }

    private async Task WriteIndexAtomicallyAsync(
        string indexFilePath,
        string sourceFolder,
        IReadOnlyList<AiIndexedManual> manuals,
        CancellationToken cancellationToken)
    {
        var temporaryPath = indexFilePath + ".tmp";
        try
        {
            var document = new AiManualIndexDocument
            {
                Version = AiManualIndexDocument.CurrentVersion,
                BuiltAt = nowProvider(),
                SourceFolder = sourceFolder,
                Manuals = manuals,
            };
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, indexFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<AiManualIndexDocument> ReadExistingIndexAsync(
        string indexFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(indexFilePath))
        {
            return new AiManualIndexDocument();
        }

        try
        {
            await using var stream = File.OpenRead(indexFilePath);
            return await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream, JsonOptions, cancellationToken)
                ?? new AiManualIndexDocument();
        }
        catch (JsonException)
        {
            return new AiManualIndexDocument();
        }
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

    private static async Task<string> ComputeFileSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildArchiveSourcePath(string archivePath, string entryPath) =>
        $"{Path.GetFullPath(archivePath)}!/{entryPath.Replace('\\', '/')}";

    private static void AddContentDiagnostic(
        ICollection<string> diagnostics,
        string sourcePath,
        ManualDocumentFilterResult classification)
    {
        if (classification.Scores is null)
        {
            return;
        }

        diagnostics.Add($"{sourcePath}: {classification.Reason}");
    }

    private static IEnumerable<AiIndexedManual> CreateManualChunks(
        ManualSourceDescriptor source,
        ManualDocumentContent content)
    {
        var text = content.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var isMarkdown = IsMarkdown(source.Extension, content.DocumentType);
        var sections = content.Pages is { Count: > 0 }
            ? content.Pages.Select(static page => new ManualSection(string.Empty, page.Text, page.PageNumber)).ToList()
            : content.Sections is { Count: > 0 }
                ? content.Sections.Select(static section => new ManualSection(section.Heading, section.Text, null)).ToList()
                : isMarkdown
                    ? SplitMarkdownSections(text).ToList()
                    : [new ManualSection(string.Empty, text, null)];

        var chunkIndex = 0;
        foreach (var section in sections)
        {
            foreach (var chunk in SplitIntoChunks(section.Text))
            {
                if (string.IsNullOrWhiteSpace(chunk.Text))
                {
                    continue;
                }

                var id = BuildId(source.SourcePath, chunkIndex, section.SectionTitle);
                var parsedPageHash = HashText(section.Text);
                yield return new AiIndexedManual
                {
                    Id = id,
                    FilePath = source.SourcePath,
                    FileName = source.FileName,
                    Title = BuildTitle(source.FileName, section.SectionTitle),
                    DocumentTitle = Path.GetFileNameWithoutExtension(source.OriginalFileName ?? source.FileName),
                    DocumentType = content.DocumentType,
                    SectionTitle = section.SectionTitle,
                    Heading = string.IsNullOrWhiteSpace(section.SectionTitle) ? null : section.SectionTitle,
                    PageNumber = section.PageNumber,
                    ChunkId = id,
                    DocumentId = source.ArchivePath ?? source.SourcePath,
                    Text = chunk.Text,
                    LastModifiedAt = source.LastModifiedAt,
                    SourceUpdatedAt = source.LastModifiedAt,
                    SourceType = "Manual",
                    Sha256 = source.Sha256,
                    ContentHash = source.Sha256,
                    ArchivePath = source.ArchivePath,
                    EntryPath = source.EntryPath,
                    OriginalFileName = source.OriginalFileName,
                    Extension = source.Extension,
                    UncompressedSize = source.UncompressedSize,
                    CompressedSize = source.CompressedSize,
                    LogicalSourceId = source.LogicalSourceId,
                    LogicalSourceLocator = source.LogicalSourceLocator,
                    ParsedSourceAddress = source.LogicalSourceId is null || source.ParsedArtifactKey is null
                        ? null
                        : new ParsedSourceAddress
                        {
                            LogicalSourceId = source.LogicalSourceId,
                            ArtifactKey = source.ParsedArtifactKey,
                            PageNumber = section.PageNumber,
                            ContentHash = parsedPageHash,
                        },
                    ChunkLocator = source.LogicalSourceId is null
                        ? null
                        : new ChunkLocator
                        {
                            LogicalSourceId = source.LogicalSourceId,
                            ChunkOrdinal = chunkIndex,
                            PageNumber = section.PageNumber,
                            SectionTitle = string.IsNullOrWhiteSpace(section.SectionTitle) ? null : section.SectionTitle,
                            OffsetBasis = section.PageNumber is not null
                                ? "ParsedPageTextUtf16"
                                : string.IsNullOrWhiteSpace(section.SectionTitle) ? "ParsedSourceTextUtf16" : "ParsedSectionTextUtf16",
                            StartOffset = chunk.StartOffset,
                            Length = chunk.Text.Length,
                            ContentHash = HashText(chunk.Text),
                        },
                    IndexLookupKey = ReadOnlyIndexRecordResolver.CreateIndexLookupKey("Manual", id),
                };
                chunkIndex += 1;
            }
        }
    }

    private static bool IsMarkdown(string extension, string documentType)
    {
        return string.Equals(documentType, "Markdown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
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
                    yield return new ManualSection(currentTitle, builder.ToString(), null);
                    builder.Clear();
                }

                currentTitle = match.Groups["title"].Value.Trim();
                hasHeading = true;
            }

            builder.AppendLine(line);
        }

        if (builder.Length > 0)
        {
            yield return new ManualSection(currentTitle, builder.ToString(), null);
        }
        else if (!hasHeading && !string.IsNullOrWhiteSpace(text))
        {
            yield return new ManualSection(string.Empty, text, null);
        }
    }

    private static IEnumerable<ChunkSlice> SplitIntoChunks(string text)
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
                yield return new ChunkSlice(chunk, start);
            }

            if (start + length >= text.Length)
            {
                break;
            }

            start += Math.Max(1, ChunkMaxLength - ChunkOverlapLength);
        }
    }

    private static string BuildTitle(string fileName, string sectionTitle)
    {
        var fileTitle = Path.GetFileNameWithoutExtension(fileName);
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

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string FindCorpusRoot(string sourcePath, IReadOnlyList<string> roots) =>
        roots.FirstOrDefault(root => IsUnderRoot(sourcePath, root)) ?? Path.GetDirectoryName(sourcePath) ?? sourcePath;

    private static bool IsUnderRoot(string sourcePath, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullSource = Path.GetFullPath(sourcePath);
        return fullSource.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullSource, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddProvenance(
        ManualSourceDescriptor source,
        ManualDocumentContent content,
        string productName,
        ICollection<SourceRegistryEntry> entries,
        ICollection<ParsedSourceArtifact> artifacts)
    {
        if (source.LogicalSourceLocator is null || source.LogicalSourceId is null || source.ParsedArtifactKey is null)
        {
            return;
        }

        entries.Add(new SourceRegistryEntry
        {
            LogicalSourceId = source.LogicalSourceId,
            ProductName = productName,
            SourceType = "Manual",
            LogicalLocator = source.LogicalSourceLocator,
            ContentHash = source.Sha256,
            ParsedArtifactKey = source.ParsedArtifactKey,
            ParserVersion = $"ManualDocumentTextExtractor:{content.DocumentType}",
        });
        var pages = content.Pages is { Count: > 0 }
            ? content.Pages.Select(static page => new ParsedSourcePage
            {
                PageNumber = page.PageNumber,
                Text = page.Text,
                ContentHash = HashText(page.Text),
            }).ToList()
            : [new ParsedSourcePage { Text = content.Text, ContentHash = HashText(content.Text) }];
        artifacts.Add(new ParsedSourceArtifact
        {
            LogicalSourceId = source.LogicalSourceId,
            ArtifactKey = source.ParsedArtifactKey,
            ContentHash = HashText(content.Text),
            ParserVersion = $"ManualDocumentTextExtractor:{content.DocumentType}",
            Pages = pages,
        });
    }

    private static string GetProductNameHint(string indexFolder)
    {
        var folder = new DirectoryInfo(Path.GetFullPath(indexFolder));
        return string.Equals(folder.Parent?.Name, "products", StringComparison.OrdinalIgnoreCase)
            ? folder.Name
            : string.Empty;
    }

    private sealed record ManualSourceDescriptor(
        string SourcePath,
        string FileName,
        string Extension,
        DateTimeOffset? LastModifiedAt,
        string Sha256,
        string? ArchivePath,
        string? EntryPath,
        string? OriginalFileName,
        long? UncompressedSize,
        long? CompressedSize,
        LogicalSourceLocator? LogicalSourceLocator,
        string? LogicalSourceId,
        string? ParsedArtifactKey)
    {
        public static ManualSourceDescriptor ForFile(string filePath, string sha256, string corpusRoot)
        {
            var fullPath = Path.GetFullPath(filePath);
            var fileInfo = new FileInfo(fullPath);
            global::SupportCaseManager.Ai.Core.Indexing.LogicalSourceLocator.TryCreateManual(corpusRoot, fullPath, out var locator);
            var logicalSourceId = locator.Value.Length == 0
                ? null
                : global::SupportCaseManager.Ai.Core.Indexing.LogicalSourceLocator.CreateLogicalSourceId(string.Empty, "Manual", locator);
            return new ManualSourceDescriptor(
                fullPath,
                fileInfo.Name,
                ManualDocumentFilter.NormalizeExtension(fileInfo.Extension),
                fileInfo.LastWriteTime,
                sha256,
                null,
                null,
                fileInfo.Name,
                fileInfo.Length,
                null,
                locator.Value.Length == 0 ? null : locator,
                logicalSourceId,
                logicalSourceId is null ? null : $"parsed:{logicalSourceId}:{sha256[..16]}");
        }

        public static ManualSourceDescriptor ForArchiveEntry(SafeZipManualEntry entry, string sourcePath, string corpusRoot)
        {
            global::SupportCaseManager.Ai.Core.Indexing.LogicalSourceLocator.TryCreateArchiveEntry(corpusRoot, entry.ArchivePath, entry.EntryPath, out var locator);
            var logicalSourceId = locator.Value.Length == 0
                ? null
                : global::SupportCaseManager.Ai.Core.Indexing.LogicalSourceLocator.CreateLogicalSourceId(string.Empty, "Manual", locator);
            return new(
                sourcePath,
                entry.OriginalFileName,
                entry.Extension,
                entry.LastModifiedAt,
                entry.Sha256,
                entry.ArchivePath,
                entry.EntryPath,
                entry.OriginalFileName,
                entry.UncompressedSize,
                entry.CompressedSize,
                locator.Value.Length == 0 ? null : locator,
                logicalSourceId,
                logicalSourceId is null ? null : $"parsed:{logicalSourceId}:{entry.Sha256[..16]}");
        }
    }

    private sealed record ManualSection(string SectionTitle, string Text, int? PageNumber);
    private sealed record ChunkSlice(string Text, int StartOffset);
}
