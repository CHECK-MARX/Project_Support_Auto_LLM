using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiIndexedNote
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("caseFolderPath")]
    public string CaseFolderPath { get; init; } = string.Empty;

    [JsonPropertyName("caseFolderName")]
    public string CaseFolderName { get; init; } = string.Empty;

    [JsonPropertyName("companyName")]
    public string? CompanyName { get; init; }

    [JsonPropertyName("supportNumber")]
    public string? SupportNumber { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("receptionDate")]
    public DateOnly? ReceptionDate { get; init; }

    [JsonPropertyName("noteKind")]
    public string NoteKind { get; init; } = string.Empty;

    [JsonPropertyName("noteFilePath")]
    public string NoteFilePath { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("lastModifiedAt")]
    public DateTimeOffset? LastModifiedAt { get; init; }
}
