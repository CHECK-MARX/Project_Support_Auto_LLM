using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Search;

internal static class ProcedureSearchBoost
{
    private static readonly string[] QueryTerms = ["生成", "作成", "方法", "手順", "アップロード", "generate", "create", "upload", "how"];
    private static readonly string[] SourceTerms = ["生成", "作成", "手順", "設定", "同期", "出力", "アップロード", "generate", "create", "upload"];
    private static readonly string[] InstructionSignals =
    [
        "以下のメニュー",
        "クリック",
        "選択します",
        "実行します",
        "コマンドを使用",
        "例:",
        "例：",
        "［",
        "--",
        " > ",
        "] > [",
    ];

    public static double Calculate(string query, params string?[] contentParts)
    {
        if (!QueryTerms.Any(term => query.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        var content = string.Join(" ", contentParts.Where(static part => !string.IsNullOrWhiteSpace(part)));
        if (query.Contains("Validate", StringComparison.OrdinalIgnoreCase) &&
            (query.Contains("アップロード", StringComparison.OrdinalIgnoreCase) ||
             query.Contains("upload", StringComparison.OrdinalIgnoreCase)))
        {
            return CalculateValidateUploadBoost(query, content);
        }

        var subjects = Regex.Matches(query, @"[A-Za-z][A-Za-z0-9_.-]{1,}")
            .Select(static match => match.Value)
            .Where(static value => value.Length >= 3 && !string.Equals(value, "how", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (subjects.Count == 0)
        {
            return 0;
        }

        if (!InstructionSignals.Any(signal => content.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        foreach (var subject in subjects)
        {
            var searchStart = 0;
            while (searchStart < content.Length)
            {
                var subjectIndex = content.IndexOf(subject, searchStart, StringComparison.OrdinalIgnoreCase);
                if (subjectIndex < 0)
                {
                    break;
                }

                var windowStart = Math.Max(0, subjectIndex - 60);
                var windowLength = Math.Min(120, content.Length - windowStart);
                var window = content.Substring(windowStart, windowLength);
                if (SourceTerms.Any(term => window.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    var boost = 0.22;
                    if (query.Contains("GUI", StringComparison.OrdinalIgnoreCase) &&
                        (content.Contains("QA·GUI", StringComparison.OrdinalIgnoreCase) ||
                         content.Contains("QA GUI", StringComparison.OrdinalIgnoreCase)))
                    {
                        boost += 0.06;
                    }

                    if (query.Contains("CLI", StringComparison.OrdinalIgnoreCase) &&
                        (content.Contains("qacli", StringComparison.OrdinalIgnoreCase) ||
                         content.Contains("QA·CLI", StringComparison.OrdinalIgnoreCase)))
                    {
                        boost += 0.06;
                    }

                    return Math.Min(0.34, boost);
                }

                searchStart = subjectIndex + subject.Length;
            }
        }

        return 0;
    }

    private static double CalculateValidateUploadBoost(string query, string content)
    {
        var compact = Regex.Replace(content, @"[\s\p{P}\p{S}]+", string.Empty);
        var hasGuiProcedure =
            (compact.Contains("ポータルValidate解析結果をアップロード", StringComparison.OrdinalIgnoreCase) ||
             compact.Contains("PortalsValidateUploadResults", StringComparison.OrdinalIgnoreCase)) &&
            (content.Contains("［", StringComparison.Ordinal) || content.Contains(" > ", StringComparison.Ordinal));
        var hasCliProcedure =
            compact.Contains("qaclivalidatebuild", StringComparison.OrdinalIgnoreCase) &&
            (compact.Contains("qafproject", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("--", StringComparison.Ordinal));

        var queryNeedsGui = query.Contains("GUI", StringComparison.OrdinalIgnoreCase);
        var queryNeedsCli = query.Contains("CLI", StringComparison.OrdinalIgnoreCase);
        if ((queryNeedsGui && hasGuiProcedure) && (queryNeedsCli && hasCliProcedure))
        {
            return 0.34;
        }

        return (queryNeedsGui && hasGuiProcedure) || (queryNeedsCli && hasCliProcedure)
            ? 0.22
            : 0;
    }
}
