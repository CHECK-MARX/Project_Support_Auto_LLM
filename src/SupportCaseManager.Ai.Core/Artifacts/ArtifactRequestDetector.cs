using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed partial class ArtifactRequestDetector
{
    public bool IsExplicitExcelTranslationRequest(string instruction)
    {
        return IsExplicitArtifactTranslationRequest(instruction)
            && ExcelRegex().IsMatch(instruction);
    }

    public bool IsExplicitArtifactTranslationRequest(string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return false;
        }

        return SupportedFileRegex().IsMatch(instruction)
            && TranslationRegex().IsMatch(instruction)
            && CreationRegex().IsMatch(instruction);
    }

    public string? FindMentionedSourceFileName(string instruction)
    {
        var match = SupportedFileNameRegex().Match(instruction ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value.Trim().Trim('「', '」', '『', '』', '"', '\'');
        foreach (var prefix in new[] { "添付ファイルの", "添付の", "ファイルの" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    public string? FindMentionedExcelFileName(string instruction)
    {
        var match = ExcelFileNameRegex().Match(instruction ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value.Trim().Trim('「', '」', '『', '』', '"', '\'');
        foreach (var prefix in new[] { "添付ファイルの", "添付の", "ファイルの" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return name;
    }

    [GeneratedRegex(@"(?:\.xlsx|Excel|エクセル)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcelRegex();

    [GeneratedRegex(@"(?:英訳|英語|翻訳|translate|translation|English)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TranslationRegex();

    [GeneratedRegex(@"(?:作成|保存|別名|コピー|ファイル名|create|save|copy|rename)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreationRegex();

    [GeneratedRegex(@"(?<name>[^\\/:*?""<>|\r\n]{1,120}\.xlsx)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcelFileNameRegex();

    [GeneratedRegex(@"(?:\.xlsx|\.csv|\.txt|\.md|Excel|エクセル|CSV|テキスト|Markdown)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedFileRegex();

    [GeneratedRegex(@"(?<name>[^\\/:*?""<>|\r\n]{1,120}\.(?:xlsx|csv|txt|md))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedFileNameRegex();
}
