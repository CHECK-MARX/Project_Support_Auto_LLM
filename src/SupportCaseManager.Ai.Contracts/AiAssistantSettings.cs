using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class AiAssistantSettings
{
    [JsonPropertyName("aiDataFolder")]
    public string AiDataFolder { get; init; } = string.Empty;

    [JsonPropertyName("aiIndexFolder")]
    public string AiIndexFolder { get; init; } = string.Empty;

    [JsonPropertyName("baseFolder")]
    public string? BaseFolder { get; init; }

    [JsonPropertyName("closeFolder")]
    public string? CloseFolder { get; init; }

    [JsonPropertyName("manualFolder")]
    public string? ManualFolder { get; init; }

    [JsonPropertyName("defaultProductName")]
    public string? DefaultProductName { get; init; }

    [JsonPropertyName("uiLanguage")]
    public string UiLanguage { get; init; } = "ja-JP";

    [JsonPropertyName("useDarkMode")]
    public bool UseDarkMode { get; init; }

    [JsonPropertyName("maxEvidenceItems")]
    public int MaxEvidenceItems { get; init; } = 2;

    [JsonPropertyName("autoSelectMinimumScore")]
    public double AutoSelectMinimumScore { get; init; } = 0.30;

    [JsonPropertyName("minimumDisplayScore")]
    public double MinimumDisplayScore { get; init; }

    [JsonPropertyName("maxPromptChars")]
    public int MaxPromptChars { get; init; } = 6000;

    [JsonPropertyName("enableCloudLlm")]
    public bool EnableCloudLlm { get; init; }

    [JsonPropertyName("maskSensitiveDataForCloud")]
    public bool MaskSensitiveDataForCloud { get; init; } = true;

    [JsonPropertyName("disableThinking")]
    public bool DisableThinking { get; init; } = true;

    [JsonPropertyName("skipGenerationWhenNoEvidence")]
    public bool SkipGenerationWhenNoEvidence { get; init; } = true;

    [JsonPropertyName("enableTopNFallback")]
    public bool EnableTopNFallback { get; init; } = true;

    [JsonPropertyName("llmProvider")]
    public LlmProviderSettings LlmProvider { get; init; } = new();

    [JsonPropertyName("products")]
    public IReadOnlyList<ProductKnowledgeSettings> Products { get; init; } = [];

    [JsonPropertyName("supportToolSettingsFilePath")]
    public string? SupportToolSettingsFilePath { get; init; }

    [JsonPropertyName("selectedProductName")]
    public string? SelectedProductName { get; init; }
}
