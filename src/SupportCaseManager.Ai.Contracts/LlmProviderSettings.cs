using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class LlmProviderSettings
{
    public const int DefaultContextWindowTokens = 8192;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "Ollama";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = "http://localhost:11434";

    [JsonPropertyName("chatModel")]
    public string ChatModel { get; init; } = "qwen3:14b";

    [JsonPropertyName("embeddingModel")]
    public string? EmbeddingModel { get; init; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 120;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.2;

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; init; } = 800;

    [JsonPropertyName("contextWindowTokens")]
    public int ContextWindowTokens { get; init; } = DefaultContextWindowTokens;

    [JsonPropertyName("apiKeyEnvironmentVariable")]
    public string? ApiKeyEnvironmentVariable { get; init; }
}
