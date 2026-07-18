using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public static class AnswerQualityModes
{
    public const string Fast = "Fast";
    public const string Standard = "Standard";
    public const string Quality = "Quality";
    public const string Custom = "Custom";
}

public static class ThinkingParameterTypes
{
    public const string None = "None";
    public const string Boolean = "Boolean";
    public const string PromptPrefix = "PromptPrefix";
}

public static class StructuredOutputModes
{
    public const string PlainText = "PlainText";
    public const string Json = "Json";
}

public sealed record class ModelCapabilityProfile
{
    [JsonPropertyName("modelName")]
    public string ModelName { get; init; } = string.Empty;

    [JsonPropertyName("modelFamily")]
    public string ModelFamily { get; init; } = string.Empty;

    [JsonPropertyName("primaryUse")]
    public string PrimaryUse { get; init; } = string.Empty;

    [JsonPropertyName("thinkingParameterType")]
    public string ThinkingParameterType { get; init; } = ThinkingParameterTypes.None;

    [JsonPropertyName("thinkingValue")]
    public string ThinkingValue { get; init; } = string.Empty;

    [JsonPropertyName("structuredOutputMode")]
    public string StructuredOutputMode { get; init; } = StructuredOutputModes.PlainText;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.2;

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; init; } = 800;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 600;

    [JsonPropertyName("maxPromptChars")]
    public int MaxPromptChars { get; init; } = 8000;

    [JsonPropertyName("recommendedEvidenceCount")]
    public int RecommendedEvidenceCount { get; init; } = 3;
}
