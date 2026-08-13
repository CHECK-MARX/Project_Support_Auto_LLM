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

    [JsonPropertyName("useQuestionAwareEvidenceSelection")]
    public bool UseQuestionAwareEvidenceSelection { get; init; }

    [JsonPropertyName("evidenceRankingMode")]
    public string EvidenceRankingMode { get; init; } = EvidenceRankingModes.Phase15;

    [JsonPropertyName("useAnswerQualityGate")]
    public bool UseAnswerQualityGate { get; init; }

    [JsonPropertyName("usePhase175QualityControls")]
    public bool UsePhase175QualityControls { get; init; }

    [JsonPropertyName("useCoverageAwareEvidenceSelection")]
    public bool UseCoverageAwareEvidenceSelection { get; init; }

    [JsonPropertyName("coverageAwareMaxEvidenceItems")]
    public int CoverageAwareMaxEvidenceItems { get; init; } = 5;

    [JsonPropertyName("useRustEvidenceSelector")]
    public bool UseRustEvidenceSelector { get; init; } = true;

    [JsonPropertyName("usePersistentRustEvidenceSelector")]
    public bool UsePersistentRustEvidenceSelector { get; init; }

    [JsonPropertyName("maxWorkerRestartsPerMinute")]
    public int MaxWorkerRestartsPerMinute { get; init; } = 3;

    [JsonPropertyName("enableRustSelectorShadowMode")]
    public bool EnableRustSelectorShadowMode { get; init; }

    [JsonPropertyName("rustEvidenceSelectorTimeoutMs")]
    public int RustEvidenceSelectorTimeoutMs { get; init; } = 2000;

    [JsonPropertyName("rustEvidenceSelectorExecutablePath")]
    public string RustEvidenceSelectorExecutablePath { get; init; } = string.Empty;

    [JsonPropertyName("shadowMinimumRunsForReadiness")]
    public int ShadowMinimumRunsForReadiness { get; init; } = 50;

    [JsonPropertyName("shadowMaxStoredRecords")]
    public int ShadowMaxStoredRecords { get; init; } = 500;

    [JsonPropertyName("llmProvider")]
    public LlmProviderSettings LlmProvider { get; init; } = new();

    [JsonPropertyName("products")]
    public IReadOnlyList<ProductKnowledgeSettings> Products { get; init; } = [];

    [JsonPropertyName("supportToolSettingsFilePath")]
    public string? SupportToolSettingsFilePath { get; init; }

    [JsonPropertyName("selectedProductName")]
    public string? SelectedProductName { get; init; }

    [JsonPropertyName("answerQualityMode")]
    public string AnswerQualityMode { get; init; } = AnswerQualityModes.Custom;

    [JsonPropertyName("modelCapabilityProfiles")]
    public IReadOnlyList<ModelCapabilityProfile> ModelCapabilityProfiles { get; init; } = [];

    [JsonPropertyName("codexExecutablePath")]
    public string CodexExecutablePath { get; init; } = string.Empty;

    [JsonPropertyName("useRagLabEvidence")]
    public bool UseRagLabEvidence { get; init; }

    [JsonPropertyName("ragLabEvidenceFilePath")]
    public string RagLabEvidenceFilePath { get; init; } = string.Empty;

    [JsonPropertyName("ragLabBaselineReadinessFilePath")]
    public string RagLabBaselineReadinessFilePath { get; init; } = string.Empty;

    [JsonPropertyName("ragLabEvidenceMaxItems")]
    public int RagLabEvidenceMaxItems { get; init; } = 3;
}
