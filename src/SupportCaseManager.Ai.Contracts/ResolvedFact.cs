using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class ResolvedFact
{
    [JsonPropertyName("factId")]
    public string FactId { get; init; } = string.Empty;

    [JsonPropertyName("statement")]
    public string Statement { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Missing";

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "Low";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("sourceUrls")]
    public IReadOnlyList<string> SourceUrls { get; init; } = [];

    [JsonPropertyName("explanation")]
    public string Explanation { get; init; } = string.Empty;

    [JsonPropertyName("product")]
    public string Product { get; init; } = string.Empty;

    [JsonPropertyName("feature")]
    public string Feature { get; init; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = string.Empty;

    [JsonPropertyName("evidenceId")]
    public string EvidenceId { get; init; } = string.Empty;

    [JsonPropertyName("documentTitle")]
    public string DocumentTitle { get; init; } = string.Empty;

    [JsonPropertyName("page")]
    public int? Page { get; init; }

    [JsonPropertyName("section")]
    public string Section { get; init; } = string.Empty;

    [JsonPropertyName("authorityLevel")]
    public string AuthorityLevel { get; init; } = string.Empty;

    [JsonPropertyName("conflictState")]
    public string ConflictState { get; init; } = string.Empty;
}
