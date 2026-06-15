using System.Text.Json.Serialization;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Drafts;

public sealed record class AiDraftDocument
{
    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; init; }

    [JsonPropertyName("case")]
    public CaseContext Case { get; init; } = new();

    [JsonPropertyName("inquiryText")]
    public string InquiryText { get; init; } = string.Empty;

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

    [JsonPropertyName("provider")]
    public AiDraftProviderSummary Provider { get; init; } = new();
}
