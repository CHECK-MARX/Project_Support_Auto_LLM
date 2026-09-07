using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class CaseCodexOverride
{
    [JsonPropertyName("supportId")]
    public string SupportId { get; init; } = string.Empty;

    [JsonPropertyName("productId")]
    public Guid? ProductId { get; init; }

    [JsonPropertyName("caseFolder")]
    public string CaseFolder { get; init; } = string.Empty;

    [JsonPropertyName("caseCodexModel")]
    public string CaseCodexModel { get; init; } = string.Empty;

    [JsonPropertyName("caseCodexReasoningEffort")]
    public string CaseCodexReasoningEffort { get; init; } = string.Empty;
}
