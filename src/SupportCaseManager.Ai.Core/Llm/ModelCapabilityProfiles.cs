using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Llm;

public static class ModelCapabilityProfiles
{
    private static readonly IReadOnlyList<ModelCapabilityProfile> Defaults =
    [
        new()
        {
            ModelName = "qwen3:8b",
            ModelFamily = "Qwen3",
            PrimaryUse = "FastDraft",
            ThinkingParameterType = ThinkingParameterTypes.Boolean,
            ThinkingValue = "false",
            StructuredOutputMode = StructuredOutputModes.Json,
            Temperature = 0.2,
            MaxOutputTokens = 600,
            TimeoutSeconds = 300,
            MaxPromptChars = 6000,
            RecommendedEvidenceCount = 2,
        },
        new()
        {
            ModelName = "gemma4:26b",
            ModelFamily = "Gemma4",
            PrimaryUse = "StandardDraft",
            ThinkingParameterType = ThinkingParameterTypes.None,
            StructuredOutputMode = StructuredOutputModes.PlainText,
            Temperature = 0.2,
            MaxOutputTokens = 800,
            TimeoutSeconds = 600,
            MaxPromptChars = 8000,
            RecommendedEvidenceCount = 3,
        },
        new()
        {
            ModelName = "gemma4:31b",
            ModelFamily = "Gemma4",
            PrimaryUse = "QualityDraft",
            ThinkingParameterType = ThinkingParameterTypes.None,
            StructuredOutputMode = StructuredOutputModes.PlainText,
            Temperature = 0.15,
            MaxOutputTokens = 1000,
            TimeoutSeconds = 900,
            MaxPromptChars = 10000,
            RecommendedEvidenceCount = 3,
        },
        new()
        {
            ModelName = "gpt-oss:120b-cloud",
            ModelFamily = "GPT-OSS",
            PrimaryUse = "CloudQualityDraft",
            ThinkingParameterType = ThinkingParameterTypes.None,
            StructuredOutputMode = StructuredOutputModes.PlainText,
            Temperature = 0.15,
            MaxOutputTokens = 1000,
            TimeoutSeconds = 900,
            MaxPromptChars = 10000,
            RecommendedEvidenceCount = 3,
        },
    ];

    public static IReadOnlyList<ModelCapabilityProfile> GetDefaults() => Defaults;

    public static ModelCapabilityProfile Resolve(
        string? modelName,
        IReadOnlyList<ModelCapabilityProfile>? overrides = null)
    {
        var normalized = modelName?.Trim() ?? string.Empty;
        var profile = Find(overrides, normalized) ?? Find(Defaults, normalized);
        if (profile is not null)
        {
            return profile;
        }

        return new ModelCapabilityProfile
        {
            ModelName = normalized,
            ModelFamily = "Custom",
            PrimaryUse = "CustomDraft",
            ThinkingParameterType = ThinkingParameterTypes.None,
            StructuredOutputMode = StructuredOutputModes.PlainText,
        };
    }

    public static string ModelForQualityMode(string? qualityMode)
    {
        return qualityMode switch
        {
            AnswerQualityModes.Fast => "qwen3:8b",
            AnswerQualityModes.Quality => "gemma4:31b",
            AnswerQualityModes.Standard => "gemma4:26b",
            _ => string.Empty,
        };
    }

    private static ModelCapabilityProfile? Find(
        IReadOnlyList<ModelCapabilityProfile>? profiles,
        string modelName)
    {
        if (profiles is null || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        return profiles.FirstOrDefault(profile => ModelNamesMatch(profile.ModelName, modelName));
    }

    private static bool ModelNamesMatch(string configured, string actual)
    {
        if (string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(RemoveLatestTag(configured), RemoveLatestTag(actual), StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveLatestTag(string value)
    {
        return value.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? value[..^7]
            : value;
    }
}
