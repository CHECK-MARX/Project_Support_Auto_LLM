using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Drafts;

public sealed record class AiDraftProviderSummary
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("chatModel")]
    public string ChatModel { get; init; } = string.Empty;

    [JsonPropertyName("embeddingModel")]
    public string? EmbeddingModel { get; init; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; init; }

    [JsonPropertyName("contextWindowTokens")]
    public int ContextWindowTokens { get; init; }

    [JsonPropertyName("apiKeyEnvironmentVariable")]
    public string? ApiKeyEnvironmentVariable { get; init; }
}
