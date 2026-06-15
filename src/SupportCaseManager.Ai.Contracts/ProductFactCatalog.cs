using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class ProductFactCatalog
{
    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("candidateFacts")]
    public IReadOnlyList<CandidateFact> CandidateFacts { get; init; } = [];

    [JsonPropertyName("resolvedFacts")]
    public IReadOnlyList<ResolvedFact> ResolvedFacts { get; init; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}
