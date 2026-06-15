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

    public async Task<AiCaseIndexBuildResult> BuildAsync(
        string sourceFolder,
        string aiIndexFolder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder))
        {
            throw new ArgumentException("AI index folder is required.", nameof(aiIndexFolder));
        }

        Directory.CreateDirectory(aiIndexFolder);
        var indexFilePath = Path.Combine(aiIndexFolder, IndexFileName);
        var warnings = new List<string>();
        var indexedNotes = new List<AiIndexedNote>();
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
                var context = await caseContextBuilder.BuildFromCaseFolderAsync(caseFolderPath, cancellationToken: cancellationToken);
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errorCount += 1;
                warnings.Add($"Failed to index case folder: {caseFolderPath}. {ex.GetType().Name}: {ex.Message}");
            }
        }

        await WriteIndexAsync(indexFilePath, sourceFolder, indexedNotes, cancellationToken);
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
