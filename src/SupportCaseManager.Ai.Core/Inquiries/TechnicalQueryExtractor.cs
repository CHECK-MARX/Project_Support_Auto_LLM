using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Inquiries;

/// <summary>Builds a retrieval-safe query and keeps recipient details out of technical scoring.</summary>
public static partial class TechnicalQueryExtractor
{
    public static (string TechnicalText, RecipientContext RecipientContext) Separate(
        string inquiryText,
        CaseContext? context)
    {
        var lines = inquiryText.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var technical = new List<string>();
        var signatures = new List<string>();
        var quotedMessage = false;
        var signatureBlock = false;
        foreach (var line in lines)
        {
            if (QuotedMessageSeparatorRegex().IsMatch(line))
            {
                quotedMessage = true;
                signatures.Add(line);
                continue;
            }

            if (quotedMessage || signatureBlock || line.StartsWith('>'))
            {
                signatures.Add(line);
                continue;
            }

            if (IsSignatureBoundary(line))
            {
                signatureBlock = true;
                signatures.Add(line);
                continue;
            }

            if (IsRecipientOnlyLine(line) || IsMailHeader(line))
            {
                signatures.Add(line);
                continue;
            }

            technical.Add(RemoveInlineRecipientData(line, context));
        }

        var email = EmailRegex().Match(inquiryText).Value;
        var phone = PhoneNumberRegex().Match(inquiryText).Value;
        var support = SupportRegex().Match(inquiryText).Value;
        return (
            string.Join(Environment.NewLine, technical.Where(static line => !string.IsNullOrWhiteSpace(line))).Trim(),
            new RecipientContext
            {
                CompanyName = context?.CompanyName,
                CustomerName = context?.CustomerName,
                SupportId = string.IsNullOrWhiteSpace(context?.SupportNumber) ? NullIfBlank(support) : context.SupportNumber,
                Email = NullIfBlank(email),
                Phone = NullIfBlank(phone),
                Signature = signatures.Count == 0 ? null : string.Join(Environment.NewLine, signatures.Take(8)),
                AnswerRecipient = context?.CustomerName,
            });
    }

    public static TechnicalQuery Extract(string technicalText, TopicEntityCatalog catalog, IReadOnlyList<string> negatedTopics)
    {
        var profile = TopicEntityAnalyzer.Extract(technicalText, catalog);
        var entities = profile.Entities;
        var technology = Match(technicalText, "Microsoft SQL Server", "SQL Server", "MS SQL Server", "MSSQL");
        var language = Match(technicalText, "T-SQL", "Transact-SQL", "Transact SQL", "PL/SQL", "PLSQL").ToList();
        if (technology.Count > 0 && !language.Contains("T-SQL", StringComparer.OrdinalIgnoreCase))
        {
            // SQL Server's language identifier is a taxonomy alias, not a generated procedure claim.
            language.Add("T-SQL");
        }
        return new TechnicalQuery
        {
            Product = profile.Products,
            Component = profile.Components,
            Feature = profile.Features,
            Operation = profile.Operations,
            Object = profile.Objects,
            Technology = technology,
            Language = language,
            Version = Values(entities, TopicEntityKind.Version),
            EnginePack = Match(technicalText, "Engine Pack", "EP"),
            Hotfix = Match(technicalText, "Hotfix", "HF"),
            ErrorCode = Values(entities, TopicEntityKind.ErrorCode),
            Command = Values(entities, TopicEntityKind.Command),
            Option = Values(entities, TopicEntityKind.Option),
            FileExtension = Values(entities, TopicEntityKind.File)
                .Select(Path.GetExtension).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Intent = profile.Intents,
            NegatedTopics = negatedTopics,
            CoreQuestion = technicalText.Trim(),
        };
    }

    private static IReadOnlyList<string> Match(string value, params string[] terms) => terms
        .Where(term => value.Contains(term, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IReadOnlyList<string> Values(IReadOnlyList<TopicEntityValue> entities, TopicEntityKind kind) => entities
        .Where(entity => entity.Kind == kind).Select(entity => entity.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsRecipientOnlyLine(string line) =>
        !ContainsTechnicalSignal(line) && (EmailRegex().IsMatch(line) || PhoneNumberRegex().IsMatch(line) || SupportRegex().IsMatch(line) ||
        PostalCodeRegex().IsMatch(line) || AddressRegex().IsMatch(line) || DateTimeRegex().IsMatch(line) ||
        line.EndsWith("株式会社", StringComparison.Ordinal) || line.EndsWith("御中", StringComparison.Ordinal) ||
        line.EndsWith("様", StringComparison.Ordinal) || line.Contains("いつもお世話になっております", StringComparison.Ordinal) ||
        BusinessClosingRegex().IsMatch(line) ||
        line.Contains('|') || CompanyIntroductionRegex().IsMatch(line) || SignatureSeparatorRegex().IsMatch(line));

    private static bool IsSignatureBoundary(string line) =>
        !ContainsTechnicalSignal(line) && BusinessClosingRegex().IsMatch(line);

    private static bool ContainsTechnicalSignal(string line) =>
        line.Contains('？') || line.Contains('?') || line.Contains("ですか", StringComparison.Ordinal) ||
        line.Contains("対象", StringComparison.Ordinal) || line.Contains("手順", StringComparison.Ordinal) ||
        line.Contains("エラー", StringComparison.Ordinal) || line.Contains("設定", StringComparison.Ordinal);

    private static bool IsMailHeader(string line) => HeaderRegex().IsMatch(line);

    private static string RemoveInlineRecipientData(string line, CaseContext? context)
    {
        var cleaned = EmailRegex().Replace(line, string.Empty);
        cleaned = PhoneNumberRegex().Replace(cleaned, string.Empty);
        cleaned = PostalCodeRegex().Replace(cleaned, string.Empty);
        cleaned = AddressRegex().Replace(cleaned, string.Empty);
        cleaned = DateTimeRegex().Replace(cleaned, string.Empty);
        cleaned = LabeledRecipientRegex().Replace(cleaned, string.Empty);
        cleaned = CompanyNameRegex().Replace(cleaned, string.Empty);
        foreach (var value in new[] { context?.CompanyName, context?.CustomerName, context?.SupportNumber })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                cleaned = cleaned.Replace(value, string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
        return cleaned.Trim(' ', '、', ',', '，', '：', ':');
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
    [GeneratedRegex(@"(?<!\d)0\d{1,4}[- ]\d{2,4}[- ]\d{3,4}(?!\d)|内線\s*\d{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumberRegex();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:SO|SR|T)\s*/?\s*\d{3,10}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportRegex();
    [GeneratedRegex(@"^(?:from|to|cc|bcc|subject|date|件名|差出人|宛先)\s*[:：]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();
    [GeneratedRegex(@"(?:株式会社|有限会社|合同会社).{0,32}(?:です|と申します|より)", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyIntroductionRegex();
    [GeneratedRegex(@"(?:株式会社|有限会社|合同会社)[^\s、,，:：。]{1,48}?(?=(?:の|様|御中|担当者|電話|$|\s))", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyNameRegex();
    [GeneratedRegex(@"(?:担当者(?:名)?|ご担当者|氏名|お名前)\s*[:：]\s*(?:[一-龯々ぁ-んァ-ヶー]{1,16}(?:\s+[一-龯々ぁ-んァ-ヶー]{1,16})?|[A-Za-z][A-Za-z .'-]{1,48})", RegexOptions.CultureInvariant)]
    private static partial Regex LabeledRecipientRegex();
    [GeneratedRegex(@"(?:〒\s*)?\d{3}-\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCodeRegex();
    [GeneratedRegex(@"(?:都|道|府|県)[^\n]{0,64}(?:市|区|町|村|丁目|番地|号)", RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();
    [GeneratedRegex(@"(?:\d{4}[/-]\d{1,2}[/-]\d{1,2}|\d{4}年\d{1,2}月\d{1,2}日)(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeRegex();
    [GeneratedRegex(@"^(?:[-_]{3,}|-----Original Message-----|-----転送メッセージ-----)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedMessageSeparatorRegex();
    [GeneratedRegex(@"^(?:[-_]{3,}|以上[、。]?$|よろしくお願いいたします[。]*$)", RegexOptions.CultureInvariant)]
    private static partial Regex SignatureSeparatorRegex();
    [GeneratedRegex(@"(?:お忙しいところ恐縮ですが|何卒よろしく(?:お願いいたします|お願いします)|よろしく(?:お願いいたします|お願いします|お願い申し上げます))。?$", RegexOptions.CultureInvariant)]
    private static partial Regex BusinessClosingRegex();
}
