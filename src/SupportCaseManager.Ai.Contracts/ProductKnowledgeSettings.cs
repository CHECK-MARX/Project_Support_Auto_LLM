using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class ProductKnowledgeSettings
{
    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("baseFolder")]
    public string BaseFolder { get; init; } = string.Empty;

    [JsonPropertyName("closeFolder")]
    public string CloseFolder { get; init; } = string.Empty;

    [JsonPropertyName("manualFolders")]
    public IReadOnlyList<string> ManualFolders { get; init; } = [];

    [JsonPropertyName("documentUrls")]
    public IReadOnlyList<string> DocumentUrls { get; init; } = [];

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;
}
