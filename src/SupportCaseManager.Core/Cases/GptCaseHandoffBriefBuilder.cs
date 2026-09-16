using System.Text.RegularExpressions;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Core.Cases;

public sealed record GptCaseHandoffSource(
    CaseRecord Case,
    string Product,
    string CustomerInquiryHistory,
    string CustomerReplyHistory,
    string ManufacturerHistory,
    IReadOnlyList<string> RelatedFileNames);

public sealed partial class GptCaseHandoffBriefBuilder
{
    private const int MaxSectionCharacters = 4500;

    public string Build(GptCaseHandoffSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Case);

        var customerEntries = CaseNoteHistoryParser.Parse(source.CustomerInquiryHistory);
        var replyEntries = CaseNoteHistoryParser.Parse(source.CustomerReplyHistory);
        var manufacturerEntries = CaseNoteHistoryParser.Parse(source.ManufacturerHistory);
        var currentDelta = CaseNoteHistoryParser.PickLatest(customerEntries);
        var lines = new List<string>
        {
            "【案件識別】",
            $"案件キー：{SafeSingleLine(source.Case.FolderName)}",
            $"製品：{SafeSingleLine(source.Product)}",
            $"Support ID：{SafeSingleLine(source.Case.SupportNumber)}",
            $"会社名：{SafeSingleLine(source.Case.Company)}",
            $"受付日：{FormatDate(source.Case.CreatedOn)}",
            $"現在状態：{SafeSingleLine(source.Case.Status)}",
            string.Empty,
            "【現在の最新問い合わせ】",
            SectionText(currentDelta is null ? [] : [currentDelta]),
            string.Empty,
            "【現在の未解決事項】",
            currentDelta is null
                ? "現在の問い合わせ履歴を特定できません。"
                : "上記の最新問い合わせを現在の確認対象として扱ってください。",
            string.Empty,
            "【回答済み・解決済み事項】",
            SectionText(Newest(replyEntries, 4)),
            string.Empty,
            "【メーカーとのこれまでのやり取り】",
            SectionText(Newest(manufacturerEntries, 3)),
            string.Empty,
            "【お客様へこれまで回答した内容】",
            SectionText(Newest(replyEntries, 3)),
            string.Empty,
            "【関連ファイル】",
        };

        var relatedFiles = source.RelatedFileNames
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        lines.Add(relatedFiles.Count == 0
            ? "関連ファイルは特定できません。"
            : string.Join(Environment.NewLine, relatedFiles.Select(name => $"- {name}")));
        lines.AddRange([
            string.Empty,
            "【現在の次アクション】",
            string.IsNullOrWhiteSpace(source.Case.Status) ? "未設定" : SafeSingleLine(source.Case.Status),
            string.Empty,
            "【GPTへの引継ぎ】",
            "この案件について、上記の履歴を前提として今後の相談に対応してください。",
            "過去に回答済みの事項と現在の未解決事項を混同しないでください。",
            "案件情報にない事実を推測せず、不足情報がある場合は明示してください。",
        ]);

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<CaseHistoryEntry> Newest(
        IReadOnlyList<CaseHistoryEntry> entries,
        int count) => entries
        .OrderByDescending(entry => entry.Timestamp ?? DateTime.MinValue)
        .ThenByDescending(entry => entry.Index)
        .Take(count);

    private static string SectionText(IEnumerable<CaseHistoryEntry> entries)
    {
        var values = entries
            .Select(entry => Sanitize(entry.Body))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(4)
            .ToList();
        if (values.Count == 0)
        {
            return "該当する履歴はありません。";
        }

        var combined = string.Join(Environment.NewLine + Environment.NewLine, values);
        return combined.Length <= MaxSectionCharacters
            ? combined
            : combined[..MaxSectionCharacters] + Environment.NewLine + "（以降省略）";
    }

    private static string Sanitize(string value)
    {
        var sanitized = EmailRegex().Replace(value ?? string.Empty, "[メールアドレス省略]");
        sanitized = PhoneRegex().Replace(sanitized, "[電話番号省略]");
        var lines = sanitized.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var signatureStart = Array.FindIndex(lines, IsSignatureStart);
        var contentLines = signatureStart >= 0 ? lines[..signatureStart] : lines;
        return string.Join(Environment.NewLine, contentLines.Where(line => !IsBoilerplate(line))).Trim();
    }

    private static bool IsSignatureStart(string line)
    {
        var value = line.Trim();
        return value.StartsWith("Best regards", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Regards", StringComparison.OrdinalIgnoreCase)
            || value.Equals("敬具", StringComparison.Ordinal);
    }

    private static bool IsBoilerplate(string line)
    {
        var value = line.Trim();
        return value.Contains("confidential", StringComparison.OrdinalIgnoreCase)
            || value.Contains("unsubscribe", StringComparison.OrdinalIgnoreCase)
            || value.Contains("オンラインセミナー", StringComparison.Ordinal);
    }

    private static string SafeSingleLine(string? value) =>
        Sanitize(value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

    private static string FormatDate(string value) =>
        DateTime.TryParseExact(value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("yyyy/MM/dd")
            : SafeSingleLine(value);

    [GeneratedRegex(@"(?<![\w.])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d[\d() -]{7,}\d)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();
}
