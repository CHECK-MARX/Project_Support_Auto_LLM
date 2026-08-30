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

    [JsonPropertyName("answerQuality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnswerQualityEvaluationResult? AnswerQuality { get; init; }

    [JsonPropertyName("readiness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Readiness { get; init; }

    [JsonPropertyName("deterministicAnswerCreated")]
    public bool DeterministicAnswerCreated { get; init; }

    [JsonPropertyName("answerGenerationMode")]
    public string AnswerGenerationMode { get; init; } = string.Empty;

    [JsonPropertyName("claims")]
    public IReadOnlyList<Claim> Claims { get; init; } = [];

    [JsonPropertyName("referenceAvailable")]
    public int ReferenceAvailable { get; init; }

    [JsonPropertyName("referenceDisplayed")]
    public int ReferenceDisplayed { get; init; }

    [JsonPropertyName("referenceMissingFromIndex")]
    public int ReferenceMissingFromIndex { get; init; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }
}
