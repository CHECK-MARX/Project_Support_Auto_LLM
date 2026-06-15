using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class AnswerDraftResult
{
    [JsonPropertyName("customerReplyDraft")]
    public string CustomerReplyDraft { get; init; } = string.Empty;

    [JsonPropertyName("internalMemo")]
    public string InternalMemo { get; init; } = string.Empty;

    [JsonPropertyName("needConfirmations")]
    public IReadOnlyList<NeedConfirmationItem> NeedConfirmations { get; init; } = [];

    [JsonPropertyName("evidence")]
    public IReadOnlyList<EvidenceItem> Evidence { get; init; } = [];

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }
}
