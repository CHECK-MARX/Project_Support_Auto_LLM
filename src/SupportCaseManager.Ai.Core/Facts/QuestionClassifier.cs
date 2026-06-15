using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Facts;

public sealed partial class QuestionClassifier : IQuestionClassifier
{
    public QuestionClassificationResult Classify(string inquiryText, InquiryFocus? inquiryFocus = null)
    {
        var text = string.IsNullOrWhiteSpace(inquiryText) ? string.Empty : inquiryText;
        var normalized = Normalize(text);
        var questionTypes = new List<string>();
        var requestedFacts = new List<string>();

        var currentInstalledVersion = ExtractCurrentInstalledVersion(text);
        var asksLatest = ContainsAny(normalized, "最新", "currentversion", "latestversion", "latest", "最新版");
        var mentionsSast = ContainsAny(normalized, "sast", "cxsast");
        var mentionsEnginePack = ContainsAny(normalized, "enginepack", "ep", "エンジンパック");
        var mentionsHotfix = ContainsAny(normalized, "hotfix", "hf", "ホットフィックス");
        var asksUpgrade = ContainsAny(normalized, "アップグレード", "アップデート", "upgrade", "update", "移行", "上げ", "更新可能", "可能ですか");
        var asksHowTo = ContainsAny(normalized, "手順", "方法", "howto", "設定", "対応手順");

        if (asksLatest && (mentionsSast || mentionsEnginePack || mentionsHotfix || ContainsAny(normalized, "バージョン", "version")))
        {
            questionTypes.Add(QuestionTypes.LatestVersionQuestion);
            requestedFacts.Add(FactKeys.LatestSastVersion);
            if (mentionsEnginePack || ContainsAny(normalized, "ep"))
            {
                requestedFacts.Add(FactKeys.LatestEnginePackVersion);
            }

            if (mentionsHotfix || ContainsAny(normalized, "hf"))
            {
                requestedFacts.Add(FactKeys.LatestHotfixVersion);
            }
        }

        if (asksUpgrade)
        {
            questionTypes.Add(QuestionTypes.UpgradePossibilityQuestion);
            requestedFacts.Add(FactKeys.UpgradePossibility);
        }

        if (asksHowTo && questionTypes.Count == 0)
        {
            questionTypes.Add(QuestionTypes.HowToQuestion);
        }

        if (questionTypes.Count == 0)
        {
            questionTypes.Add(QuestionTypes.GeneralSupportQuestion);
        }

        if (!string.IsNullOrWhiteSpace(currentInstalledVersion) &&
            requestedFacts.Contains(FactKeys.UpgradePossibility, StringComparer.OrdinalIgnoreCase))
        {
            requestedFacts.Add(FactKeys.CurrentInstalledVersion);
        }

        return new QuestionClassificationResult
        {
            QuestionTypes = questionTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CurrentInstalledVersion = currentInstalledVersion,
            RequestedFacts = requestedFacts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Explanation = BuildExplanation(questionTypes, currentInstalledVersion, requestedFacts),
        };
    }

    private static string BuildExplanation(
        IReadOnlyList<string> questionTypes,
        string currentInstalledVersion,
        IReadOnlyList<string> requestedFacts)
    {
        var builder = new StringBuilder();
        builder.Append("QuestionTypes=");
        builder.Append(string.Join(",", questionTypes));
        builder.Append("; RequestedFacts=");
        builder.Append(string.Join(",", requestedFacts));
        if (!string.IsNullOrWhiteSpace(currentInstalledVersion))
        {
            builder.Append("; CurrentInstalledVersion=");
            builder.Append(currentInstalledVersion);
        }

        return builder.ToString();
    }

    private static string ExtractCurrentInstalledVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        foreach (Match match in CurrentVersionRegex().Matches(text))
        {
            var value = BuildSastVersion(match);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return NormalizeVersionDisplay(value);
            }
        }

        foreach (Match match in SastVersionWithHotfixRegex().Matches(text))
        {
            var start = Math.Max(0, match.Index - 16);
            var prefix = text[start..match.Index];
            if (ContainsAny(Normalize(prefix), "現在", "利用中", "使用中", "現行", "installed"))
            {
                return NormalizeVersionDisplay(BuildSastVersion(match));
            }
        }

        return string.Empty;
    }

    private static string NormalizeVersionDisplay(string value)
    {
        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    private static string BuildSastVersion(Match match)
    {
        var version = match.Groups["version"].Value.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var hotfix = match.Groups["hotfix"].Value.Trim();
        return string.IsNullOrWhiteSpace(hotfix)
            ? $"SAST {version}"
            : $"SAST {version} {NormalizeVersionDisplay(hotfix.ToUpperInvariant())}";
    }

    private static bool ContainsAny(string normalized, params string[] terms)
    {
        return terms.Any(term => normalized.Contains(Normalize(term), StringComparison.Ordinal));
    }

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

    [GeneratedRegex(@"(?:(?:現在|利用中|使用中|現行)[^\r\n。]{0,40})?(?:Checkmarx\s*)?(?:SAST|CxSAST)(?:\s*[（(]\s*Cx?SAST\s*[）)])?\s*(?<version>\d+\.\d+\.\d+)(?:\s*(?<hotfix>HF\s*\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentVersionRegex();

    [GeneratedRegex(@"(?:SAST|CxSAST)(?:\s*[（(]\s*Cx?SAST\s*[）)])?\s*(?<version>\d+\.\d+\.\d+)(?:\s*(?<hotfix>HF\s*\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SastVersionWithHotfixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
