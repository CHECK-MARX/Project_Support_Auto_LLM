using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class GptHandoffContext
{
    [JsonPropertyName("supportId")]
    public string SupportId { get; init; } = string.Empty;

    [JsonPropertyName("product")]
    public string Product { get; init; } = string.Empty;

    [JsonPropertyName("targetGptKey")]
    public string TargetGptKey { get; init; } = string.Empty;

    [JsonPropertyName("targetGptDisplayName")]
    public string TargetGptDisplayName { get; init; } = string.Empty;

    [JsonPropertyName("conversationUrl")]
    public string ConversationUrl { get; init; } = string.Empty;

    [JsonPropertyName("registeredAt")]
    public string RegisteredAt { get; init; } = string.Empty;

    [JsonPropertyName("linkMode")]
    public string LinkMode { get; init; } = string.Empty;

    [JsonPropertyName("registrationState")]
    public string RegistrationState { get; init; } = string.Empty;

    [JsonPropertyName("lastImportedHash")]
    public string LastImportedHash { get; init; } = string.Empty;

    [JsonPropertyName("lastImportedAt")]
    public string LastImportedAt { get; init; } = string.Empty;

    [JsonPropertyName("importVersion")]
    public int ImportVersion { get; init; }
}
