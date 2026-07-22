using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class SupportToolProductSettings
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];

    [JsonPropertyName("baseFolder")]
    public string BaseFolder { get; init; } = string.Empty;

    [JsonPropertyName("closeFolder")]
    public string CloseFolder { get; init; } = string.Empty;

    [JsonPropertyName("productPromptFilePath")]
    public string ProductPromptFilePath { get; init; } = string.Empty;

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }
}
