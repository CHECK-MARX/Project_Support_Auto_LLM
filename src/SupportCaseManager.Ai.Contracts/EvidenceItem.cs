using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class EvidenceItem
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("documentTitle")]
    public string? DocumentTitle { get; init; }

    [JsonPropertyName("pageNumber")]
    public int? PageNumber { get; init; }

    [JsonPropertyName("sectionTitle")]
    public string? SectionTitle { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; init; }

    [JsonPropertyName("documentId")]
    public string? DocumentId { get; init; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("archivePath")]
    public string? ArchivePath { get; init; }

    [JsonPropertyName("entryPath")]
    public string? EntryPath { get; init; }

    [JsonPropertyName("excerpt")]
    public string Excerpt { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    [JsonPropertyName("supportNumber")]
    public string? SupportNumber { get; init; }

    [JsonPropertyName("relevance")]
    public double Relevance { get; init; }

    [JsonPropertyName("evidenceRole")]
    public string EvidenceRole { get; init; } = "Supporting";
}
