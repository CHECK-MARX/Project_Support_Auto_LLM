using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ArtifactFilenamePreview
{
    public string SourceBaseName { get; init; } = string.Empty;
    public string TranslatedBaseName { get; init; } = string.Empty;
    public string OutputFileName { get; init; } = string.Empty;
    public bool UsedFallback { get; init; }
    public string Warning { get; init; } = string.Empty;
}

public sealed class ArtifactFilenameTranslationService
{
    private const int MaximumBaseNameLength = 100;

    private static readonly (string Source, string Translation)[] KnownPhrases =
    [
        ("追加問い合わせ内容", "Additional Inquiry Details"),
        ("問い合わせ内容", "Inquiry Details"),
        ("プロジェクト設定手順", "Project Settings Guide"),
        ("利用権限", "Access Permissions"),
        ("確認手順", "Verification Procedure"),
        ("スキャン結果", "Scan Results"),
        ("調査メモ", "Investigation Notes"),
    ];

    private static readonly (string Source, string Translation)[] KnownTokens =
    [
        ("追加", "Additional"),
        ("問い合わせ", "Inquiry"),
        ("内容", "Details"),
        ("スキャン", "Scan"),
        ("結果", "Results"),
        ("調査", "Investigation"),
        ("メモ", "Notes"),
        ("質問", "Question"),
        ("回答", "Answer"),
        ("資料", "Document"),
        ("報告書", "Report"),
        ("概要", "Summary"),
        ("設定", "Settings"),
        ("プロジェクト", "Project"),
        ("環境", "Environment"),
        ("ログ", "Log"),
        ("エラー", "Error"),
        ("画面", "Screen"),
        ("手順", "Procedure"),
        ("確認", "Check"),
        ("ライセンス", "License"),
        ("利用", "Usage"),
        ("権限", "Permissions"),
        ("追記", "Addendum"),
        ("更新", "Updated"),
        ("改訂", "Revised"),
        ("補足", "Supplement"),
    ];

    public ArtifactFilenamePreview CreatePreview(string sourceFilePath)
    {
        var extension = Path.GetExtension(sourceFilePath);
        var sourceBaseName = Path.GetFileNameWithoutExtension(sourceFilePath);
        var translatedBaseName = Translate(sourceBaseName);
        var usedFallback = false;
        var warning = string.Empty;

        if (ContainsJapanese(translatedBaseName))
        {
            // Preserve translated words and source identifiers instead of discarding the whole name.
            translatedBaseName = Regex.Replace(translatedBaseName,
                @"[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff\uff66-\uff9f]+", match =>
                    $" Untranslated_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(match.Value)))[..12]} ");
            usedFallback = true;
            warning = "ファイル名の一部を英訳できませんでした。未翻訳部分を識別子に置き換え、英訳できた語句・番号・日付は保持しています。必要に応じて出力ファイル名を編集してください。";
        }

        var normalized = Normalize(translatedBaseName);
        if (string.IsNullOrWhiteSpace(sourceBaseName)
            || string.IsNullOrWhiteSpace(normalized))
        {
            usedFallback = true;
            warning = "ファイル名の英訳候補を生成できなかったため、安全な既定名を使用します。必要に応じて編集してください。";
            normalized = "Translated_File";
        }

        if (!HasEnglishTranslationMarker(normalized))
        {
            normalized = TrimToMaximumLength(normalized, MaximumBaseNameLength - "_EN".Length);
            normalized = $"{normalized}_EN";
        }
        else
        {
            normalized = TrimToMaximumLength(normalized, MaximumBaseNameLength);
        }
        return new ArtifactFilenamePreview
        {
            SourceBaseName = sourceBaseName,
            TranslatedBaseName = normalized,
            OutputFileName = normalized + extension,
            UsedFallback = usedFallback,
            Warning = warning,
        };
    }

    private static string Translate(string sourceBaseName)
    {
        var translated = sourceBaseName;
        foreach (var (source, translation) in KnownPhrases)
        {
            translated = translated.Replace(source, $" {translation} ", StringComparison.Ordinal);
        }

        foreach (var (source, translation) in KnownTokens)
        {
            translated = translated.Replace(source, $" {translation} ", StringComparison.Ordinal);
        }

        return translated;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasUnderscore = false;
        foreach (var character in value.Trim())
        {
            var normalized = character switch
            {
                var c when char.IsAsciiLetterOrDigit(c) => c,
                '_' or '-' or '.' or '~' => character,
                var c when char.IsWhiteSpace(c) => '_',
                _ => '_',
            };

            if (normalized == '_')
            {
                if (previousWasUnderscore)
                {
                    continue;
                }

                previousWasUnderscore = true;
            }
            else
            {
                previousWasUnderscore = false;
            }

            builder.Append(normalized);
        }

        return builder.ToString().Trim(' ', '.', '_');
    }

    private static bool HasEnglishTranslationMarker(string value)
    {
        var marker = value.LastIndexOf("_EN", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var suffix = value[(marker + 3)..];
        if (suffix.Length == 0)
        {
            return true;
        }

        return suffix.StartsWith('_')
            && suffix[1..].Split('_', StringSplitOptions.RemoveEmptyEntries)
                .All(static part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static bool ContainsJapanese(string value)
    {
        return value.Any(static character =>
            char.IsLetter(character)
            && (character is >= '\u3040' and <= '\u30ff'
                or >= '\u3400' and <= '\u9fff'
                or >= '\uf900' and <= '\ufaff'
                or >= '\uff66' and <= '\uff9f'));
    }

    private static string TrimToMaximumLength(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value[..maximumLength].TrimEnd(' ', '.', '_');
    }
}
