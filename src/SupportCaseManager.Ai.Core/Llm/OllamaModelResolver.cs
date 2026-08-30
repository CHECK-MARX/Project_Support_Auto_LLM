using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Llm;

public static class OllamaModelResolver
{
    public static ModelResolutionResult Resolve(
        string? savedModel,
        string? qualityMode,
        IReadOnlyList<string> availableModels)
    {
        var requestedModel = savedModel?.Trim() ?? string.Empty;
        var available = availableModels
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (available.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(savedModel))
            {
                return new ModelResolutionResult
                {
                    Model = requestedModel,
                    RequestedModel = requestedModel,
                    Source = ModelResolutionSources.Saved,
                    AvailableModels = available,
                    Message = "モデル一覧を取得できないため、保存済みモデルを保持します。",
                };
            }

            return new ModelResolutionResult
            {
                RequestedModel = requestedModel,
                Source = ModelResolutionSources.Unresolved,
                AvailableModels = available,
                FallbackReason = ModelFallbackReasons.NoAvailableModels,
                Message = "利用可能なOllamaモデルがありません。",
            };
        }

        var restored = Find(available, savedModel);
        if (!string.IsNullOrWhiteSpace(restored))
        {
            return Success(restored, ModelResolutionSources.Saved, available, requestedModel);
        }

        var preset = ModelCapabilityProfiles.ModelForQualityMode(qualityMode ?? string.Empty);
        var resolvedPreset = Find(available, preset);
        if (!string.IsNullOrWhiteSpace(resolvedPreset))
        {
            return string.IsNullOrWhiteSpace(requestedModel)
                ? Success(resolvedPreset, ModelResolutionSources.Preset, available, requestedModel)
                : Success(
                    resolvedPreset,
                    ModelResolutionSources.Fallback,
                    available,
                    requestedModel,
                    resolvedPreset,
                    ModelFallbackReasons.RequestedModelUnavailable);
        }

        foreach (var candidate in FallbackOrder(qualityMode))
        {
            var fallback = Find(available, candidate);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return Success(
                    fallback,
                    ModelResolutionSources.Fallback,
                    available,
                    requestedModel,
                    fallback,
                    string.IsNullOrWhiteSpace(requestedModel)
                        ? ModelFallbackReasons.QualityPresetUnavailable
                        : ModelFallbackReasons.RequestedModelUnavailable);
            }
        }

        return Success(
            available[0],
            ModelResolutionSources.Fallback,
            available,
            requestedModel,
            available[0],
            string.IsNullOrWhiteSpace(requestedModel)
                ? ModelFallbackReasons.QualityPresetUnavailable
                : ModelFallbackReasons.RequestedModelUnavailable);
    }

    private static ModelResolutionResult Success(
        string model,
        string source,
        IReadOnlyList<string> available,
        string requestedModel,
        string fallbackModel = "",
        string fallbackReason = "")
    {
        return new ModelResolutionResult
        {
            Model = model,
            RequestedModel = requestedModel,
            FallbackModel = fallbackModel,
            FallbackReason = fallbackReason,
            Source = source,
            AvailableModels = available,
            Message = $"回答モデルを解決しました。Model={model}; Source={source}",
        };
    }

    private static string? Find(IReadOnlyList<string> models, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        return models.FirstOrDefault(model => ModelNameMatches(model, requested));
    }

    private static IReadOnlyList<string> FallbackOrder(string? qualityMode)
    {
        if (string.Equals(qualityMode, AnswerQualityModes.Fast, StringComparison.OrdinalIgnoreCase))
        {
            return ["qwen3:8b", "qwen3:4b", "gemma4:26b", "gemma4:31b"];
        }

        if (string.Equals(qualityMode, AnswerQualityModes.Quality, StringComparison.OrdinalIgnoreCase))
        {
            return ["gemma4:31b", "gemma4:26b", "qwen3:8b", "qwen3:4b"];
        }

        return ["gemma4:31b", "qwen3:8b", "qwen3:4b", "gemma4:26b"];
    }

    private static bool ModelNameMatches(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.Replace(":latest", string.Empty, StringComparison.OrdinalIgnoreCase), right, StringComparison.OrdinalIgnoreCase)
            || string.Equals(left, right.Replace(":latest", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record class ModelResolutionResult
{
    public string Model { get; init; } = string.Empty;

    public string RequestedModel { get; init; } = string.Empty;

    public string EffectiveModel => Model;

    public string FallbackModel { get; init; } = string.Empty;

    public string FallbackReason { get; init; } = string.Empty;

    public string Source { get; init; } = ModelResolutionSources.Unresolved;

    public IReadOnlyList<string> AvailableModels { get; init; } = [];

    public string Message { get; init; } = string.Empty;

    public bool IsResolved => !string.IsNullOrWhiteSpace(Model);
}

public static class ModelResolutionSources
{
    public const string Saved = "Saved";
    public const string Preset = "Preset";
    public const string Fallback = "Fallback";
    public const string Unresolved = "Unresolved";
}

public static class ModelFallbackReasons
{
    public const string NoAvailableModels = "NoAvailableModels";
    public const string RequestedModelUnavailable = "RequestedModelUnavailable";
    public const string QualityPresetUnavailable = "QualityPresetUnavailable";
}
