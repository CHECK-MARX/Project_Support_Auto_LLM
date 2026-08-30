using System.Globalization;
using System.Text;

namespace SupportCaseManager.Ai.Core.Facts;

internal static class FreshnessIntentPolicy
{
    public static bool IsOperationalAccessOrDeliveryInquiry(string? text)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (ContainsAny(normalized, "fiebie", "fibe", "ファイル転送"))
        {
            return true;
        }

        var asksForDeliveryOrAccess = ContainsAny(
            normalized,
            "ダウンロード",
            "入手",
            "アクセス",
            "配布",
            "提供",
            "アップロードサイト");
        var reportsOperationalProblem = ContainsAny(
            normalized,
            "できない",
            "できません",
            "失敗",
            "制限",
            "ブロック",
            "代替",
            "別の方法",
            "一つ前",
            "旧版",
            "webフィルタ",
            "プロキシ",
            "ssl検査");
        return asksForDeliveryOrAccess && reportsOperationalProblem;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(Normalize(term), StringComparison.Ordinal));

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
