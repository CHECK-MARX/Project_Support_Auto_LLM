using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class InquiryFocus
{
    [JsonPropertyName("focusText")]
    public string FocusText { get; init; } = string.Empty;

    [JsonPropertyName("importantTerms")]
    public IReadOnlyList<string> ImportantTerms { get; init; } = [];

    [JsonPropertyName("excludedTerms")]
    public IReadOnlyList<string> ExcludedTerms { get; init; } = [];

    [JsonPropertyName("targetVersions")]
    public IReadOnlyList<string> TargetVersions { get; init; } = [];

    [JsonPropertyName("isFreshnessSensitive")]
    public bool IsFreshnessSensitive { get; init; }

    [JsonPropertyName("freshnessReason")]
    public string FreshnessReason { get; init; } = string.Empty;
}
