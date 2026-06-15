using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class AnswerDraftRequest
{
    [JsonPropertyName("case")]
    public CaseContext Case { get; init; } = new();

    [JsonPropertyName("inquiryText")]
    public string InquiryText { get; init; } = string.Empty;

    [JsonPropertyName("inquiryFocus")]
    public InquiryFocus? InquiryFocus { get; init; }

    [JsonPropertyName("userInstruction")]
    public string? UserInstruction { get; init; }

    [JsonPropertyName("sources")]
    public IReadOnlyList<SearchSource> Sources { get; init; } = [];

    [JsonPropertyName("factResolution")]
    public FactResolutionResult? FactResolution { get; init; }

    [JsonPropertyName("settings")]
    public AiAssistantSettings Settings { get; init; } = new();

    [JsonPropertyName("language")]
    public string Language { get; init; } = "ja-JP";

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; init; }
}
