using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class SearchSource
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("retrievedAt")]
    public DateTimeOffset? RetrievedAt { get; init; }

    [JsonPropertyName("supportNumber")]
    public string? SupportNumber { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; init; }

    [JsonPropertyName("matchedTerms")]
    public IReadOnlyList<string> MatchedTerms { get; init; } = [];

    [JsonPropertyName("scoreBreakdown")]
    public string ScoreBreakdown { get; init; } = string.Empty;

    [JsonPropertyName("queryCoverage")]
    public string QueryCoverage { get; init; } = string.Empty;

    [JsonPropertyName("exclusionReason")]
    public string ExclusionReason { get; init; } = string.Empty;

    [JsonPropertyName("questionText")]
    public string? QuestionText { get; init; }

    [JsonPropertyName("internalMemo")]
    public string? InternalMemo { get; init; }

    [JsonPropertyName("matchKind")]
    public string? MatchKind { get; init; }

    [JsonPropertyName("documentId")]
    public string? DocumentId { get; init; }

    [JsonPropertyName("sectionTitle")]
    public string? SectionTitle { get; init; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("documentTitle")]
    public string? DocumentTitle { get; init; }

    [JsonPropertyName("pageNumber")]
    public int? PageNumber { get; init; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; init; }

    [JsonPropertyName("archivePath")]
    public string? ArchivePath { get; init; }

    [JsonPropertyName("entryPath")]
    public string? EntryPath { get; init; }
}
