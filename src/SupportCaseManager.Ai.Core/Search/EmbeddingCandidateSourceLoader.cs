using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Search;

/// <summary>
/// Rehydrates the source metadata needed after a vector-only match.  It does not
/// rank content; ranking remains in the Hybrid/Rust selector pipeline.
/// </summary>
internal static class EmbeddingCandidateSourceLoader
{
    private const int ExcerptLength = 1200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<IReadOnlyList<SearchSource>> LoadAsync(
        string productName,
        string productIndexFolder,
        CancellationToken cancellationToken)
    {
        var cases = await ReadAsync<AiIndexDocument>(
            Path.Combine(productIndexFolder, AiCaseIndexBuilder.IndexFileName), cancellationToken);
        var manuals = await ReadAsync<AiManualIndexDocument>(
            Path.Combine(productIndexFolder, AiManualIndexBuilder.IndexFileName), cancellationToken);
        var official = await ReadAsync<AiOfficialDocumentIndexDocument>(
            Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName), cancellationToken);
        var answers = await ReadAsync<CaseAnswerPairIndexDocument>(
            Path.Combine(productIndexFolder, CaseAnswerPairIndexDocument.FileName), cancellationToken);

        return (cases?.Notes.Select(note => new SearchSource
            {
                SourceId = note.Id,
                SourceType = "PastCaseNote",
                ProductName = productName,
                Title = note.Title,
                Text = Excerpt(note.Text),
                FilePath = note.NoteFilePath,
                SupportNumber = note.SupportNumber,
                RetrievedAt = note.LastModifiedAt,
                DocumentId = note.NoteFilePath,
                DocumentTitle = note.Title,
                SectionTitle = note.NoteKind,
                ChunkId = note.Id,
            }) ?? [])
            .Concat(manuals?.Manuals.Select(manual => new SearchSource
            {
                SourceId = manual.Id,
                SourceType = "Manual",
                ProductName = string.IsNullOrWhiteSpace(manual.Product) ? productName : manual.Product,
                Title = manual.Title,
                Text = Excerpt(manual.Text),
                FilePath = manual.FilePath,
                Url = manual.Url,
                RetrievedAt = manual.LastModifiedAt,
                DocumentId = manual.DocumentId ?? manual.ArchivePath ?? manual.FilePath,
                DocumentTitle = manual.DocumentTitle ?? manual.FileName,
                SectionTitle = manual.SectionTitle,
                PageNumber = manual.PageNumber,
                ChunkId = manual.ChunkId ?? manual.Id,
                ContentHash = manual.ContentHash ?? manual.Sha256,
                ArchivePath = manual.ArchivePath,
                EntryPath = manual.EntryPath,
            }) ?? [])
            .Concat(official?.Documents.Select(document => new SearchSource
            {
                SourceId = document.Id,
                SourceType = "OfficialDoc",
                ProductName = document.ProductName,
                Title = string.IsNullOrWhiteSpace(document.SectionTitle)
                    ? document.Title
                    : $"{document.Title} - {document.SectionTitle}",
                Text = Excerpt(document.Text),
                Url = document.Url,
                RetrievedAt = document.RetrievedAt,
                DocumentId = document.Url,
                DocumentTitle = document.Title,
                SectionTitle = document.SectionTitle,
                ChunkId = document.Id,
                ContentHash = document.ContentHash,
            }) ?? [])
            .Concat(answers?.Pairs.Select(pair => new SearchSource
            {
                SourceId = pair.Id,
                SourceType = "ExactPastAnswer",
                ProductName = string.IsNullOrWhiteSpace(pair.ProductName) ? productName : pair.ProductName,
                Title = pair.QuestionText,
                Text = Excerpt(pair.CustomerReplyText),
                FilePath = pair.SourceFile,
                SupportNumber = pair.SupportNumber,
                RetrievedAt = pair.UpdatedAt,
                DocumentId = pair.SourceFile,
                DocumentTitle = pair.QuestionText,
                SectionTitle = pair.NoteType,
                ChunkId = pair.Id,
                QuestionText = pair.QuestionText,
                InternalMemo = pair.InternalMemo,
            }) ?? [])
            .Where(source => string.IsNullOrWhiteSpace(source.ProductName) ||
                string.Equals(source.ProductName, productName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static source => $"{source.SourceType}\n{source.SourceId}", StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static string Excerpt(string text) => text.Length <= ExcerptLength
        ? text
        : $"{text[..ExcerptLength]}...";
}
