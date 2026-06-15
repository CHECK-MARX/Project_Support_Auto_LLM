using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class NoteSnapshot
{
    [JsonPropertyName("noteKind")]
    public string NoteKind { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("lastModifiedAt")]
    public DateTimeOffset? LastModifiedAt { get; init; }

    [JsonPropertyName("isCurrent")]
    public bool IsCurrent { get; init; }
}
