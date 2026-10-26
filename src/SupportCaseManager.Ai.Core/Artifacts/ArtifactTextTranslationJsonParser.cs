using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed class ArtifactTextTranslationJsonParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ArtifactTextTranslationParseResult ParseIdentified(string response, IReadOnlyList<ArtifactTextTranslationEntry> expectedEntries)
    {
        var expected = expectedEntries.Where(static entry => entry.ShouldTranslate).ToArray();
        var parsed = ArtifactTranslationBatchContract.Parse(response, expected.Select(ArtifactTranslationBatchContract.Id));
        if (parsed.Errors.Count > 0)
            return new ArtifactTextTranslationParseResult { Errors = parsed.Errors };
        // Identity and source text come exclusively from the approved plan, never from model output.
        return new ArtifactTextTranslationParseResult
        {
            Succeeded = true,
            Values = expected.Select(entry => new ArtifactTextTranslationValue
            {
                Key = entry.Key,
                SourceText = entry.SourceText,
                TranslatedText = parsed.Translations[ArtifactTranslationBatchContract.Id(entry)],
            }).ToArray(),
        };
    }

    public ArtifactTextTranslationParseResult Parse(
        string json,
        IReadOnlyList<ArtifactTextTranslationEntry> expectedEntries)
    {
        IReadOnlyList<ArtifactTextTranslationValue> values;
        try
        {
            values = JsonSerializer.Deserialize<List<ArtifactTextTranslationValue>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException ex)
        {
            return new ArtifactTextTranslationParseResult
            {
                Errors = [$"翻訳結果のJSONを解析できません: {ex.Message}"],
            };
        }

        var expected = expectedEntries
            .Where(static item => item.ShouldTranslate)
            .ToDictionary(static item => item.Key, StringComparer.Ordinal);
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || value.Key is null)
            {
                errors.Add("翻訳結果にnull項目またはnullキーがあります。");
                continue;
            }

            if (!seen.Add(value.Key))
            {
                errors.Add($"翻訳結果に重複項目があります: {value.Key}");
                continue;
            }

            if (!expected.TryGetValue(value.Key, out var entry))
            {
                errors.Add($"計画にない項目が翻訳結果に含まれています: {value.Key}");
                continue;
            }

            if (!string.Equals(entry.SourceText, value.SourceText, StringComparison.Ordinal))
            {
                errors.Add($"翻訳結果の原文が一致しません: {value.Key}");
            }

            if (string.IsNullOrWhiteSpace(value.TranslatedText))
            {
                errors.Add($"翻訳文が空です: {value.Key}");
            }
        }

        foreach (var entry in expected.Values)
        {
            if (!seen.Contains(entry.Key))
            {
                errors.Add($"翻訳結果に項目がありません: {entry.Key}");
            }
        }

        return new ArtifactTextTranslationParseResult
        {
            Succeeded = errors.Count == 0 && values.Count == expected.Count,
            Values = values,
            Errors = errors,
        };
    }
}

public sealed record ArtifactTextTranslationParseResult
{
    public bool Succeeded { get; init; }
    public IReadOnlyList<ArtifactTextTranslationValue> Values { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}
