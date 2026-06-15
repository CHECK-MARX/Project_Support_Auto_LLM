using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Answers;

internal static class AnswerDraftResultParser
{
    private const string ParseFailureWarning = "LLM応答のJSON解析に失敗しました。回答内容を確認してください。";

    public static ParsedAnswerDraftResult Parse(
        string response,
        IReadOnlyList<SearchSource> allowedSources)
    {
        var warnings = new List<string>();
        var sourceMap = allowedSources
            .Where(static source => !string.IsNullOrWhiteSpace(source.SourceId))
            .GroupBy(static source => source.SourceId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(response))
        {
            warnings.Add("LLM応答が空です。回答内容を確認してください。");
            return new ParsedAnswerDraftResult(new AnswerDraftResult(), warnings, HasEvidenceProperty: false);
        }

        var payload = ExtractJsonPayload(response);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add(ParseFailureWarning);
                return BuildFallback(response, warnings);
            }

            var root = document.RootElement;
            var hasEvidenceProperty = root.TryGetProperty("evidence", out var evidenceElement);
            var result = new AnswerDraftResult
            {
                CustomerReplyDraft = ReadString(root, "customerReplyDraft"),
                InternalMemo = ReadReadableText(root, "internalMemo"),
                NeedConfirmations = ReadNeedConfirmations(root, warnings),
                Evidence = hasEvidenceProperty
                    ? ReadEvidence(evidenceElement, sourceMap, warnings)
                    : [],
                Confidence = ReadDouble(root, "confidence") ?? 0,
                Warnings = ReadWarnings(root),
                GeneratedAt = ReadDateTimeOffset(root, "generatedAt") ?? default,
            };

            return new ParsedAnswerDraftResult(result, warnings, hasEvidenceProperty);
        }
        catch (JsonException)
        {
            warnings.Add(ParseFailureWarning);
            return BuildFallback(response, warnings);
        }
    }

    private static ParsedAnswerDraftResult BuildFallback(string response, IReadOnlyList<string> warnings)
    {
        var customerReplyDraft = TryExtractCustomerReplyDraft(response)
            ?? TryUseNaturalLanguageResponse(response)
            ?? "LLM応答を解析できませんでした。回答内容を確認してください。";
        return new ParsedAnswerDraftResult(
            new AnswerDraftResult
            {
                CustomerReplyDraft = customerReplyDraft,
            },
            warnings,
            HasEvidenceProperty: false);
    }

    private static string ExtractJsonPayload(string response)
    {
        var trimmed = response.Trim();
        var fencedMatch = Regex.Match(
            trimmed,
            @"```(?:json)?\s*(?<json>.*?)\s*```",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (fencedMatch.Success)
        {
            return fencedMatch.Groups["json"].Value.Trim();
        }

        var objectPayload = ExtractFirstJsonObject(trimmed);
        return string.IsNullOrWhiteSpace(objectPayload) ? trimmed : objectPayload;
    }

    private static string ExtractFirstJsonObject(string value)
    {
        var start = value.IndexOf('{');
        if (start < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var ch = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return value[start..(index + 1)];
                }
            }
        }

        return string.Empty;
    }

    private static string? TryExtractCustomerReplyDraft(string response)
    {
        var match = Regex.Match(
            response,
            @"""customerReplyDraft""\s*:\s*""(?<value>(?:\\.|[^""\\])*)""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>($"\"{match.Groups["value"].Value}\"");
        }
        catch (JsonException)
        {
            return match.Groups["value"].Value;
        }
    }

    private static string? TryUseNaturalLanguageResponse(string response)
    {
        var cleaned = RemoveThinkingBlocks(response).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        cleaned = RemoveMarkdownFence(cleaned).Trim();
        if (LooksLikeMalformedJson(cleaned))
        {
            return null;
        }

        return cleaned.Length <= 4000 ? cleaned : cleaned[..4000] + "...";
    }

    private static string RemoveThinkingBlocks(string response)
    {
        var withoutThinkBlocks = Regex.Replace(
            response,
            @"<think>.*?</think>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return Regex.Replace(
            withoutThinkBlocks,
            @"<thinking>.*?</thinking>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string RemoveMarkdownFence(string response)
    {
        var fencedMatch = Regex.Match(
            response.Trim(),
            @"```(?:text|markdown|md)?\s*(?<text>.*?)\s*```",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return fencedMatch.Success ? fencedMatch.Groups["text"].Value : response;
    }

    private static bool LooksLikeMalformedJson(string response)
    {
        var trimmed = response.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) ||
            trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return true;
        }

        if (response.Contains("\"customerReplyDraft\"", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("\"internalMemo\"", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("\"needConfirmations\"", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return response.Contains("{", StringComparison.Ordinal) ||
            response.Contains("}", StringComparison.Ordinal);
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element)
            ? ReadElementAsText(element)
            : string.Empty;
    }

    private static string ReadReadableText(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element)
            ? ReadElementAsText(element)
            : string.Empty;
    }

    private static string ReadElementAsText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Object => FormatObject(element),
            JsonValueKind.Array => FormatArray(element),
            _ => string.Empty,
        };
    }

    private static string FormatObject(JsonElement element)
    {
        var builder = new StringBuilder();
        foreach (var property in element.EnumerateObject())
        {
            var value = ReadElementAsText(property.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(property.Name);
            builder.Append(": ");
            builder.Append(value);
        }

        return builder.ToString();
    }

    private static string FormatArray(JsonElement element)
    {
        var values = element
            .EnumerateArray()
            .Select(ReadElementAsText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return values.Count == 0 ? string.Empty : string.Join(Environment.NewLine, values.Select(static value => $"- {value}"));
    }

    private static IReadOnlyList<NeedConfirmationItem> ReadNeedConfirmations(
        JsonElement root,
        List<string> warnings)
    {
        if (!root.TryGetProperty("needConfirmations", out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("needConfirmations が配列ではありません。要確認事項を読み飛ばしました。");
            return [];
        }

        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(ReadNeedConfirmation)
            .Where(static item => !string.IsNullOrWhiteSpace(item.Question) || !string.IsNullOrWhiteSpace(item.Reason))
            .ToList();
    }

    private static NeedConfirmationItem ReadNeedConfirmation(JsonElement element)
    {
        return new NeedConfirmationItem
        {
            Question = ReadString(element, "question"),
            Reason = ReadString(element, "reason"),
            Priority = NormalizePriority(ReadString(element, "priority")),
            RelatedSourceIds = ReadStringArray(element, "relatedSourceIds"),
        };
    }

    private static string NormalizePriority(string priority)
    {
        return priority switch
        {
            "High" or "Normal" or "Low" => priority,
            _ => "Normal",
        };
    }

    private static IReadOnlyList<EvidenceItem> ReadEvidence(
        JsonElement element,
        IReadOnlyDictionary<string, SearchSource> sourceMap,
        List<string> warnings)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("evidence が配列ではありません。参照根拠を読み飛ばしました。");
            return [];
        }

        var result = new List<EvidenceItem>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var sourceId = ReadString(item, "sourceId");
            if (string.IsNullOrWhiteSpace(sourceId) || !sourceMap.TryGetValue(sourceId, out var source))
            {
                warnings.Add($"LLM応答の参照根拠 sourceId='{sourceId}' は送信済みSourcesに存在しないため除外しました。");
                continue;
            }

            result.Add(new EvidenceItem
            {
                SourceId = sourceId,
                SourceType = FirstNonWhiteSpace(ReadString(item, "sourceType"), source.SourceType),
                Title = FirstNonWhiteSpace(ReadString(item, "title"), source.Title),
                Excerpt = FirstNonWhiteSpace(ReadString(item, "excerpt"), BuildExcerpt(source.Text, 240)),
                FilePath = FirstNonWhiteSpaceOrNull(ReadString(item, "filePath"), source.FilePath),
                SupportNumber = FirstNonWhiteSpaceOrNull(ReadString(item, "supportNumber"), source.SupportNumber),
                Relevance = ReadDouble(item, "relevance") ?? source.Score ?? 0,
            });
        }

        return result;
    }

    private static IReadOnlyList<string> ReadWarnings(JsonElement root)
    {
        if (!root.TryGetProperty("warnings", out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var warning = element.GetString();
            return string.IsNullOrWhiteSpace(warning) ? [] : [warning];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Select(ReadElementAsText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Select(ReadElementAsText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static double? ReadDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), out var value))
        {
            return value;
        }

        return null;
    }

    private static string FirstNonWhiteSpace(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string? FirstNonWhiteSpaceOrNull(params string?[] values)
    {
        var value = FirstNonWhiteSpace(values);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string BuildExcerpt(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }
}

internal sealed record ParsedAnswerDraftResult(
    AnswerDraftResult Result,
    IReadOnlyList<string> Warnings,
    bool HasEvidenceProperty);
