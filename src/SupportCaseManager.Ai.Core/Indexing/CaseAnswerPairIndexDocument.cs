using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class CaseAnswerPairIndexDocument
{
    public const int CurrentVersion = 1;
    public const string FileName = "case-answer-pairs-index.json";

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("builtAt")]
    public DateTimeOffset BuiltAt { get; init; }

    [JsonPropertyName("sourceFolder")]
    public string SourceFolder { get; init; } = string.Empty;

    [JsonPropertyName("pairs")]
    public IReadOnlyList<CaseAnswerPair> Pairs { get; init; } = [];
}

public sealed record class CaseAnswerPair
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("supportNumber")]
    public string SupportNumber { get; init; } = string.Empty;

    [JsonPropertyName("questionText")]
    public string QuestionText { get; init; } = string.Empty;

    [JsonPropertyName("customerReplyText")]
    public string CustomerReplyText { get; init; } = string.Empty;

    [JsonPropertyName("internalMemo")]
    public string InternalMemo { get; init; } = string.Empty;

    [JsonPropertyName("noteType")]
    public string NoteType { get; init; } = string.Empty;

    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; init; } = string.Empty;

    [JsonPropertyName("caseFolderPath")]
    public string CaseFolderPath { get; init; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("normalizedQuestion")]
    public string NormalizedQuestion { get; init; } = string.Empty;

    [JsonPropertyName("questionHash")]
    public string QuestionHash { get; init; } = string.Empty;
}
