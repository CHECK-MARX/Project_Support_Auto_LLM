using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Safety;

public sealed partial class SafetyRedactionService : ISafetyRedactionService
{
    public string RedactForLog(string input)
    {
        return RedactSensitiveText(input);
    }

    public string RedactForCloud(string input)
    {
        return RedactSensitiveText(input);
    }

    public string RemoveInternalReferencesFromCustomerReply(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var text = InternalMemoLineRegex().Replace(input, string.Empty);
        text = WindowsPathRegex().Replace(text, "[内部パス削除]");
        text = EvidenceIdRegex().Replace(text, "[根拠ID削除]");
        return text.Trim();
    }

    public IReadOnlyList<string> FindCustomerReplyWarnings(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var warnings = new List<string>();
        if (WindowsPathRegex().IsMatch(input))
        {
            warnings.Add("お客様向け回答案に内部パスらしき文字列が含まれています。");
        }

        if (EvidenceIdRegex().IsMatch(input))
        {
            warnings.Add("お客様向け回答案に根拠IDらしき文字列が含まれています。");
        }

        if (InternalMemoLineRegex().IsMatch(input))
        {
            warnings.Add("お客様向け回答案に社内メモらしき記載が含まれています。");
        }

        return warnings;
    }

    private static string RedactSensitiveText(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var text = EmailRegex().Replace(input, "[メールアドレス]");
        text = PhoneRegex().Replace(text, "[電話番号]");
        text = WindowsPathRegex().Replace(text, "[Windowsパス]");
        text = ApiKeyRegex().Replace(text, "[APIキー]");
        return text;
    }

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d{1,3}[-\s]?)?(?:0\d{1,4}[-\s]?\d{1,4}[-\s]?\d{3,4})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n\t :;""'<>|]+(?:\\[^\r\n\t :;""'<>|]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?:sk-[A-Za-z0-9_-]{12,}|Bearer\s+[A-Za-z0-9._-]{12,}|(?:api[_-]?key|apikey)\s*[:=]\s*[A-Za-z0-9._-]{8,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"\b(?:sourceId|evidenceId)\s*[:=]\s*[\w:.-]+|\b(?:case|manual):[\w:.-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceIdRegex();

    [GeneratedRegex(@"^\s*社内メモ\s*[:：].*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex InternalMemoLineRegex();
}
