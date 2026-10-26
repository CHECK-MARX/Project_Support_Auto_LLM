using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Artifacts;

/// <summary>Bind model translations to the approved local source without asking the model to copy that source.</summary>
public static class ArtifactTranslationBatchContract
{
    public static string Id(ExcelTranslationEntry entry) => Hash(["excel", entry.Sheet, entry.Cell, entry.SourceText]);
    public static string Id(ArtifactTextTranslationEntry entry) => Hash(["text", entry.Key, entry.SourceText]);

    private static string Hash(string[] identity) => "t_" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))))[..32];

    internal static (Dictionary<string, string> Translations, IReadOnlyList<string> Errors) Parse(
        string response, IEnumerable<string> expectedIds)
    {
        var expected = expectedIds.ToHashSet(StringComparer.Ordinal);
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return (translations, ["翻訳結果はJSON配列で返してください。"]);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || item.EnumerateObject().Count() != 2
                    || !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("translatedText", out var textValue) || textValue.ValueKind != JsonValueKind.String)
                {
                    errors.Add("各翻訳項目にはidとtranslatedTextだけが必要です。原文・シート名・セル名は返さないでください。");
                    continue;
                }
                var id = idValue.GetString()!;
                if (!expected.Contains(id))
                {
                    errors.Add($"計画外の翻訳IDです: {id}");
                    continue;
                }
                if (!translations.TryAdd(id, textValue.GetString()!))
                    errors.Add($"翻訳IDが重複しています: {id}");
                if (string.IsNullOrWhiteSpace(textValue.GetString()))
                    errors.Add($"翻訳文が空です: {id}");
            }
            foreach (var id in expected.Except(translations.Keys, StringComparer.Ordinal))
                errors.Add($"翻訳結果にIDがありません: {id}");
        }
        catch (JsonException)
        {
            errors.Add("翻訳結果をJSON配列として解析できません。JSON配列だけを返してください。");
        }
        return (translations, errors);
    }
}
