using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed class ExcelTranslationJsonParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ExcelTranslationParseResult Parse(
        string response,
        IReadOnlyList<ExcelTranslationEntry> expectedEntries)
    {
        var errors = new List<string>();
        IReadOnlyList<ExcelTranslationValue> values;
        try
        {
            var json = ExtractJsonArray(response);
            values = JsonSerializer.Deserialize<List<ExcelTranslationValue>>(json, SerializerOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new ExcelTranslationParseResult
            {
                Errors = [$"Codexの翻訳JSONを解析できません: {ex.Message}"],
            };
        }

        var expected = expectedEntries
            .Where(static item => item.ShouldTranslate)
            .ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = Key(value.Sheet, value.Cell);
            if (!seen.Add(key))
            {
                errors.Add($"翻訳結果に重複セルがあります: {value.Sheet}!{value.Cell}");
                continue;
            }

            if (!expected.TryGetValue(key, out var entry))
            {
                errors.Add($"計画にないセルが翻訳結果に含まれています: {value.Sheet}!{value.Cell}");
                continue;
            }

            if (!string.Equals(entry.SourceText, value.SourceText, StringComparison.Ordinal))
            {
                errors.Add($"原文が一致しません: {value.Sheet}!{value.Cell}");
            }

            if (string.IsNullOrWhiteSpace(value.TranslatedText))
            {
                errors.Add($"翻訳文が空です: {value.Sheet}!{value.Cell}");
            }
        }

        foreach (var missing in expected.Keys.Except(seen, StringComparer.OrdinalIgnoreCase))
        {
            var entry = expected[missing];
            errors.Add($"翻訳結果にセルがありません: {entry.Sheet}!{entry.Cell}");
        }

        return new ExcelTranslationParseResult
        {
            Succeeded = errors.Count == 0,
            Values = errors.Count == 0 ? values : [],
            Errors = errors,
        };
    }

    private static string ExtractJsonArray(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException("応答が空です。");
        }

        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("JSON配列がありません。");
        }

        return trimmed[start..(end + 1)];
    }

    private static string Key(ExcelTranslationEntry entry) => Key(entry.Sheet, entry.Cell);
    private static string Key(string sheet, string cell) => $"{sheet}\u001f{cell}";
}
