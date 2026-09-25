namespace SupportCaseManager.Core.Quality;

public static class QualityStyleReviewer
{
    public static string Review(string draft, string? userInstruction, int approvedExamples)
    {
        if (approvedExamples < 2) return "承認済みの文体例が2件未満のため、レビューは行いません。";
        if (string.IsNullOrWhiteSpace(draft)) return "レビュー対象の案がありません。";

        var warnings = new List<string>();
        if (draft.Split('\n').Any(line => line.Length > 400)) warnings.Add("長い段落があります。読みやすさを確認してください。");
        var lines = draft.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.GroupBy(line => line, StringComparer.Ordinal).Any(group => group.Count() > 1 && group.Key.Length > 15))
            warnings.Add("同じ文の重複を確認してください。");
        if ((userInstruction?.Contains("質問しない", StringComparison.Ordinal) == true
                || userInstruction?.Contains("追加質問", StringComparison.Ordinal) == true)
            && (draft.Contains('?') || draft.Contains('？')))
            warnings.Add("質問禁止の指示と疑問文の整合を確認してください。");
        if (draft.Contains("close the case", StringComparison.OrdinalIgnoreCase)
            || draft.Contains("案件を終了", StringComparison.Ordinal))
            warnings.Add("案件終了の意図が明示されたか確認してください。");
        return warnings.Count == 0
            ? "文体・構成の機械的な警告なし。事実、根拠、技術値の確認は既存検証と人の確認が必要です。"
            : string.Join(Environment.NewLine, warnings);
    }
}
