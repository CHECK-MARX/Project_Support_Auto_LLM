using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class CuratedFactCatalog
{
    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("latestSastVersion")]
    public string LatestSastVersion { get; init; } = string.Empty;

    [JsonPropertyName("latestEnginePackVersion")]
    public string LatestEnginePackVersion { get; init; } = string.Empty;

    [JsonPropertyName("latestHotfixVersion")]
    public string LatestHotfixVersion { get; init; } = string.Empty;

    [JsonPropertyName("sourceUrls")]
    public IReadOnlyList<string> SourceUrls { get; init; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}
