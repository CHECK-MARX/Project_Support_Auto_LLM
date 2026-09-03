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

    [JsonPropertyName("lexicalScore")]
    public double? LexicalScore { get; init; }

    [JsonPropertyName("semanticScore")]
    public double? SemanticScore { get; init; }

    [JsonPropertyName("rrfScore")]
    public double? RrfScore { get; init; }

    [JsonPropertyName("finalRerankScore")]
    public double? FinalRerankScore { get; init; }

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

    [JsonPropertyName("caseSessionId")]
    public string? CaseSessionId { get; init; }

    [JsonPropertyName("logicalFileId")]
    public string? LogicalFileId { get; init; }

    [JsonPropertyName("locator")]
    public string? Locator { get; init; }

    [JsonPropertyName("evidenceKind")]
    public string? EvidenceKind { get; init; }

    [JsonPropertyName("parseStatus")]
    public string? ParseStatus { get; init; }

    [JsonPropertyName("scanEvidenceId")]
    public string? ScanEvidenceId { get; init; }

    [JsonPropertyName("reportedLine")]
    public int? ReportedLine { get; init; }

    [JsonPropertyName("contextStartLine")]
    public int? ContextStartLine { get; init; }

    [JsonPropertyName("contextEndLine")]
    public int? ContextEndLine { get; init; }

    [JsonPropertyName("sourceRole")]
    public string? SourceRole { get; init; }

    [JsonPropertyName("sinkRole")]
    public string? SinkRole { get; init; }

    [JsonPropertyName("resultPath")]
    public string? ResultPath { get; init; }
}
