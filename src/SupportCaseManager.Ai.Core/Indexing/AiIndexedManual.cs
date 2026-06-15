using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiIndexedManual
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; init; } = string.Empty;

    [JsonPropertyName("sectionTitle")]
    public string SectionTitle { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("lastModifiedAt")]
    public DateTimeOffset? LastModifiedAt { get; init; }
}
