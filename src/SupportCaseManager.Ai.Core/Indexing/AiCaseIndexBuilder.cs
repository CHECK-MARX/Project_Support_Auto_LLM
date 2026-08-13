using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed class AiCaseIndexBuilder : IAiCaseIndexBuilder
{
    public const string IndexFileName = "case-notes-index.json";

    private const int ChunkMaxLength = 3000;
    private const int ChunkOverlapLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ICaseContextBuilder caseContextBuilder;
    private readonly Func<DateTimeOffset> nowProvider;

    public AiCaseIndexBuilder(
        ICaseContextBuilder caseContextBuilder,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.caseContextBuilder = caseContextBuilder ?? throw new ArgumentNullException(nameof(caseContextBuilder));
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public Task<AiCaseIndexBuildResult> BuildAsync(
        string sourceFolder,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        return BuildCoreAsync(sourceFolder, aiIndexFolder, productName: null, cancellationToken);
    }

    public Task<AiCaseIndexBuildResult> BuildForProductAsync(
        string sourceFolder,
        string aiIndexFolder,
        string productName,
        CancellationToken cancellationToken = default)
    {
        return BuildCoreAsync(sourceFolder, aiIndexFolder, productName, cancellationToken);
    }

    private async Task<AiCaseIndexBuildResult> BuildCoreAsync(
        string sourceFolder,
        string aiIndexFolder,
        string? productName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder))
        {
            throw new ArgumentException("AI index folder is required.", nameof(aiIndexFolder));
        }

        Directory.CreateDirectory(aiIndexFolder);
        var indexFilePath = Path.Combine(aiIndexFolder, IndexFileName);
        var warnings = new List<string>();
        var indexedNotes = new List<AiIndexedNote>();
        var indexedAnswerPairs = new List<CaseAnswerPair>();
        var scannedCaseFolderCount = 0;
        var scannedNoteFileCount = 0;
        var emptyNoteSkippedCount = 0;
        var supportNumberExtractedCount = 0;
        var missingSupportNumberCount = 0;
        var noteKindExtractedCount = 0;
        var unknownNoteKindCount = 0;
        var indexedCaseCount = 0;
        var errorCount = 0;

        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            warnings.Add("Source folder does not exist.");
            errorCount += 1;
            await WriteIndexAsync(indexFilePath, sourceFolder ?? string.Empty, indexedNotes, cancellationToken);
            await WriteAnswerPairIndexAtomicallyAsync(aiIndexFolder, sourceFolder ?? string.Empty, indexedAnswerPairs, cancellationToken);
            return new AiCaseIndexBuildResult
            {
                IndexedCaseCount = 0,
                IndexedNoteCount = 0,
                ErrorCount = errorCount,
                IndexFilePath = indexFilePath,
                Warnings = warnings,
            };
        }

        foreach (var caseFolderPath in EnumerateCaseFolders(sourceFolder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedCaseFolderCount += 1;

            try
            {
                var context = await caseContextBuilder.BuildFromCaseFolderAsync(
                    caseFolderPath,
                    productName,
                    cancellationToken: cancellationToken);
                indexedCaseCount += 1;
                var caseFolderName = Path.GetFileName(caseFolderPath);
                scannedNoteFileCount += context.Notes.Count;

                var hasSupportNumber = !string.IsNullOrWhiteSpace(context.SupportNumber);
                if (!hasSupportNumber)
                {
                    warnings.Add($"Support number could not be extracted from case folder: {caseFolderPath}");
                }

                foreach (var note in context.Notes)
                {
                    if (hasSupportNumber)
                    {
                        supportNumberExtractedCount += 1;
                    }
                    else
                    {
                        missingSupportNumberCount += 1;
                    }

                    if (string.IsNullOrWhiteSpace(note.Text))
                    {
                        emptyNoteSkippedCount += 1;
                        warnings.Add($"Skipped empty case note file: {note.FilePath}");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(note.NoteKind) ||
                        string.Equals(note.NoteKind, "Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        unknownNoteKindCount += 1;
                        warnings.Add($"Note kind could not be determined: {note.FilePath}");
                    }
                    else
                    {
                        noteKindExtractedCount += 1;
                    }

                    var chunkIndex = 0;
                    foreach (var chunk in SplitIntoChunks(note.Text))
                    {
                        indexedNotes.Add(new AiIndexedNote
                        {
                            Id = BuildId(caseFolderPath, note.FilePath, chunkIndex),
                            CaseFolderPath = caseFolderPath,
                            CaseFolderName = caseFolderName,
                            CompanyName = context.CompanyName,
                            SupportNumber = context.SupportNumber,
                            Status = context.Status,
                            ReceptionDate = context.ReceptionDate,
                            NoteKind = note.NoteKind,
                            NoteFilePath = note.FilePath,
                            Title = BuildTitle(context.SupportNumber, context.CompanyName, note.NoteKind, chunkIndex),
                            Text = chunk,
                            LastModifiedAt = note.LastModifiedAt,
                        });
                        chunkIndex += 1;
                    }
                }

                indexedAnswerPairs.AddRange(CaseAnswerPairExtractor.Extract(context, caseFolderPath, productName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errorCount += 1;
                warnings.Add($"Failed to index case folder: {caseFolderPath}. {ex.GetType().Name}: {ex.Message}");
            }
        }

        await WriteIndexAsync(indexFilePath, sourceFolder, indexedNotes, cancellationToken);
        var answerPairIndexPath = await WriteAnswerPairIndexAtomicallyAsync(
            aiIndexFolder,
            sourceFolder,
            indexedAnswerPairs,
            cancellationToken);
        return new AiCaseIndexBuildResult
        {
            ScannedCaseFolderCount = scannedCaseFolderCount,
            ScannedNoteFileCount = scannedNoteFileCount,
            EmptyNoteSkippedCount = emptyNoteSkippedCount,
            SupportNumberExtractedCount = supportNumberExtractedCount,
            MissingSupportNumberCount = missingSupportNumberCount,
            NoteKindExtractedCount = noteKindExtractedCount,
            UnknownNoteKindCount = unknownNoteKindCount,
            IndexedCaseCount = indexedCaseCount,
            IndexedNoteCount = indexedNotes.Count,
            ErrorCount = errorCount,
            IndexFilePath = indexFilePath,
            IndexedAnswerPairCount = indexedAnswerPairs.Count,
            AnswerPairIndexFilePath = answerPairIndexPath,
            Warnings = warnings,
        };
    }

    public Task<AiCaseIndexBuildResult> BuildIncrementalAsync(
        string sourceFolder,
        string aiIndexFolder,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        return BuildIncrementalCoreAsync(sourceFolder, aiIndexFolder, productName: null, forceRebuild, cancellationToken);
    }

    public Task<AiCaseIndexBuildResult> BuildIncrementalForProductAsync(
        string sourceFolder,
        string aiIndexFolder,
        string productName,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        return BuildIncrementalCoreAsync(sourceFolder, aiIndexFolder, productName, forceRebuild, cancellationToken);
    }

    private async Task<AiCaseIndexBuildResult> BuildIncrementalCoreAsync(
        string sourceFolder,
        string aiIndexFolder,
        string? productName,
        bool forceRebuild,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder))
        {
            throw new ArgumentException("AI index folder is required.", nameof(aiIndexFolder));
        }

        Directory.CreateDirectory(aiIndexFolder);
        var indexFilePath = Path.Combine(aiIndexFolder, IndexFileName);
        var answerPairIndexPath = Path.Combine(aiIndexFolder, CaseAnswerPairIndexDocument.FileName);
        var answerPairIndexExists = File.Exists(answerPairIndexPath);
        var existing = await ReadExistingIndexAsync(indexFilePath, cancellationToken);
        var existingAnswerPairs = await ReadExistingAnswerPairIndexAsync(answerPairIndexPath, cancellationToken);
        var existingByCase = existing.Notes
            .Where(static note => !string.IsNullOrWhiteSpace(note.CaseFolderPath))
            .GroupBy(static note => Path.GetFullPath(note.CaseFolderPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var existingPairsByCase = existingAnswerPairs.Pairs
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.CaseFolderPath))
            .GroupBy(static pair => Path.GetFullPath(pair.CaseFolderPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            return new AiCaseIndexBuildResult
            {
                ErrorCount = 1,
                IndexFilePath = indexFilePath,
                IndexedNoteCount = existing.Notes.Count,
                IndexedAnswerPairCount = existingAnswerPairs.Pairs.Count,
                AnswerPairIndexFilePath = answerPairIndexPath,
                Warnings = ["Source folder does not exist. Existing index was retained."],
            };
        }

        var currentCaseFolders = EnumerateCaseFolders(sourceFolder)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentCaseSet = currentCaseFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new List<AiIndexedNote>();
        var outputAnswerPairs = new List<CaseAnswerPair>();
        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var errors = 0;
        var scannedNoteFiles = 0;

        foreach (var caseFolderPath in currentCaseFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            existingByCase.TryGetValue(caseFolderPath, out var oldNotes);
            existingPairsByCase.TryGetValue(caseFolderPath, out var oldAnswerPairs);
            var currentNoteFiles = Directory.EnumerateFiles(caseFolderPath, "*.txt", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            scannedNoteFiles += currentNoteFiles.Count;

            var isUnchanged = !forceRebuild && answerPairIndexExists && oldNotes is { Count: > 0 } &&
                HasSameNoteFilesAndTimestamps(oldNotes, currentNoteFiles);
            if (isUnchanged)
            {
                output.AddRange(oldNotes!);
                if (oldAnswerPairs is { Count: > 0 })
                {
                    outputAnswerPairs.AddRange(oldAnswerPairs);
                }
                unchanged += 1;
                continue;
            }

            try
            {
                var context = await caseContextBuilder.BuildFromCaseFolderAsync(
                    caseFolderPath,
                    productName,
                    cancellationToken: cancellationToken);
                var caseFolderName = Path.GetFileName(caseFolderPath);
                foreach (var note in context.Notes.Where(static note => !string.IsNullOrWhiteSpace(note.Text)))
                {
                    var chunkIndex = 0;
                    foreach (var chunk in SplitIntoChunks(note.Text))
                    {
                        output.Add(new AiIndexedNote
                        {
                            Id = BuildId(caseFolderPath, note.FilePath, chunkIndex),
                            CaseFolderPath = caseFolderPath,
                            CaseFolderName = caseFolderName,
                            CompanyName = context.CompanyName,
                            SupportNumber = context.SupportNumber,
                            Status = context.Status,
                            ReceptionDate = context.ReceptionDate,
                            NoteKind = note.NoteKind,
                            NoteFilePath = note.FilePath,
                            Title = BuildTitle(context.SupportNumber, context.CompanyName, note.NoteKind, chunkIndex),
                            Text = chunk,
                            LastModifiedAt = note.LastModifiedAt,
                        });
                        chunkIndex += 1;
                    }
                }

                outputAnswerPairs.AddRange(CaseAnswerPairExtractor.Extract(context, caseFolderPath, productName));

                if (oldNotes is { Count: > 0 })
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
                warnings.Add($"Failed to update case folder: {caseFolderPath}. {ex.GetType().Name}: {ex.Message}");
                if (oldNotes is { Count: > 0 })
                {
                    output.AddRange(oldNotes);
                }

                if (oldAnswerPairs is { Count: > 0 })
                {
                    outputAnswerPairs.AddRange(oldAnswerPairs);
                }
            }
        }

        var deleted = existingByCase.Keys.Count(path => !currentCaseSet.Contains(path));
        await WriteIndexAtomicallyAsync(indexFilePath, sourceFolder, output, cancellationToken);
        await WriteAnswerPairIndexAtomicallyAsync(aiIndexFolder, sourceFolder, outputAnswerPairs, cancellationToken);
        return new AiCaseIndexBuildResult
        {
            ScannedCaseFolderCount = currentCaseFolders.Count,
            ScannedNoteFileCount = scannedNoteFiles,
            IndexedCaseCount = output.Select(static note => note.CaseFolderPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            IndexedNoteCount = output.Count,
            AddedCaseCount = added,
            ChangedCaseCount = changed,
            DeletedCaseCount = deleted,
            UnchangedCaseCount = unchanged,
            ErrorCount = errors,
            IndexFilePath = indexFilePath,
            IndexedAnswerPairCount = outputAnswerPairs.Count,
            AnswerPairIndexFilePath = answerPairIndexPath,
            Warnings = warnings,
        };
    }

    private async Task WriteIndexAsync(
        string indexFilePath,
        string sourceFolder,
        IReadOnlyList<AiIndexedNote> notes,
        CancellationToken cancellationToken)
    {
        var document = new AiIndexDocument
        {
            BuiltAt = nowProvider(),
            SourceFolder = sourceFolder,
            Notes = notes,
        };

        await using var stream = File.Create(indexFilePath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
    }

    private async Task WriteIndexAtomicallyAsync(
        string indexFilePath,
        string sourceFolder,
        IReadOnlyList<AiIndexedNote> notes,
        CancellationToken cancellationToken)
    {
        var temporaryPath = indexFilePath + ".tmp";
        try
        {
            var document = new AiIndexDocument
            {
                BuiltAt = nowProvider(),
                SourceFolder = sourceFolder,
                Notes = notes,
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

    private static async Task<AiIndexDocument> ReadExistingIndexAsync(
        string indexFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(indexFilePath))
        {
            return new AiIndexDocument();
        }

        try
        {
            await using var stream = File.OpenRead(indexFilePath);
            return await JsonSerializer.DeserializeAsync<AiIndexDocument>(stream, JsonOptions, cancellationToken)
                ?? new AiIndexDocument();
        }
        catch (JsonException)
        {
            return new AiIndexDocument();
        }
    }

    private async Task<string> WriteAnswerPairIndexAtomicallyAsync(
        string aiIndexFolder,
        string sourceFolder,
        IReadOnlyList<CaseAnswerPair> pairs,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(aiIndexFolder, CaseAnswerPairIndexDocument.FileName);
        var temporaryPath = $"{indexPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new CaseAnswerPairIndexDocument
            {
                BuiltAt = nowProvider(),
                SourceFolder = sourceFolder,
                Pairs = pairs
                    .OrderBy(static pair => pair.ProductName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static pair => pair.SupportNumber, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(static pair => pair.UpdatedAt)
                    .ToList(),
            };
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, indexPath, overwrite: true);
            return indexPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<CaseAnswerPairIndexDocument> ReadExistingAnswerPairIndexAsync(
        string indexFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(indexFilePath))
        {
            return new CaseAnswerPairIndexDocument();
        }

        try
        {
            await using var stream = File.OpenRead(indexFilePath);
            return await JsonSerializer.DeserializeAsync<CaseAnswerPairIndexDocument>(stream, JsonOptions, cancellationToken)
                ?? new CaseAnswerPairIndexDocument();
        }
        catch (JsonException)
        {
            return new CaseAnswerPairIndexDocument();
        }
    }

    private static bool HasSameNoteFilesAndTimestamps(
        IReadOnlyList<AiIndexedNote> oldNotes,
        IReadOnlyList<string> currentNoteFiles)
    {
        var oldByPath = oldNotes
            .Where(static note => !string.IsNullOrWhiteSpace(note.NoteFilePath))
            .GroupBy(static note => Path.GetFullPath(note.NoteFilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().LastModifiedAt, StringComparer.OrdinalIgnoreCase);
        if (oldByPath.Count != currentNoteFiles.Count)
        {
            return false;
        }

        return currentNoteFiles.All(path =>
            oldByPath.TryGetValue(path, out var oldModified) &&
            oldModified == new FileInfo(path).LastWriteTime);
    }

    private static IEnumerable<string> EnumerateCaseFolders(string sourceFolder)
    {
        var candidates = new[] { sourceFolder }
            .Concat(Directory.EnumerateDirectories(sourceFolder, "*", SearchOption.AllDirectories));

        foreach (var candidate in candidates)
        {
            if (CaseParser.ParseCaseFromDirectory(new DirectoryInfo(candidate)) is not null)
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(ChunkMaxLength, text.Length - start);
            yield return text.Substring(start, length);
            if (start + length >= text.Length)
            {
                break;
            }

            start += Math.Max(1, ChunkMaxLength - ChunkOverlapLength);
        }
    }

    private static string BuildTitle(
        string? supportNumber,
        string? companyName,
        string noteKind,
        int chunkIndex)
    {
        var title = $"{supportNumber ?? "-"} {companyName ?? ""} {noteKind}".Trim();
        return chunkIndex == 0 ? title : $"{title} chunk {chunkIndex + 1}";
    }

    private static string BuildId(string caseFolderPath, string noteFilePath, int chunkIndex)
    {
        var raw = $"{caseFolderPath}|{noteFilePath}|{chunkIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }
}
