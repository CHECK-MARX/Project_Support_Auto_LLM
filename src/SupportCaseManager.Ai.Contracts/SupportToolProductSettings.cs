using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class SupportToolProductSettings
{
    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("baseFolder")]
    public string BaseFolder { get; init; } = string.Empty;

    [JsonPropertyName("closeFolder")]
    public string CloseFolder { get; init; } = string.Empty;
}
