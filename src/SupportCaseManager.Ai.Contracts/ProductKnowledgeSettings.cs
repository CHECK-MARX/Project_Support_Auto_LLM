using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class ProductKnowledgeSettings
{
    public const int DefaultCrawlMaxDepth = 2;
    public const int DefaultCrawlMaxPages = 100;

    [JsonPropertyName("productId")]
    public Guid ProductId { get; init; }

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];

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

    [JsonPropertyName("productPromptFilePath")]
    public string ProductPromptFilePath { get; init; } = string.Empty;

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }

    [JsonPropertyName("crawlMaxDepth")]
    public int CrawlMaxDepth { get; init; } = DefaultCrawlMaxDepth;

    [JsonPropertyName("crawlMaxPages")]
    public int CrawlMaxPages { get; init; } = DefaultCrawlMaxPages;
}
