using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class NeedConfirmationItem
{
    [JsonPropertyName("question")]
    public string Question { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "Normal";

    [JsonPropertyName("relatedSourceIds")]
    public IReadOnlyList<string> RelatedSourceIds { get; init; } = [];
}
