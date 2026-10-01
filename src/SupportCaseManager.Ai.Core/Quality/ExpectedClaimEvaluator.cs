using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Quality;

/// <summary>Evaluation-only, conservative clause coverage; never supplied to generation.</summary>
public static class ExpectedClaimEvaluator
{
    private static readonly string[][] Concepts =
    [
        ["起動失敗", "起動できない", "起動できません", "起動に失敗"],
        ["移設", "移行"], ["誤検知", "false positive"],
        ["言語", "language"], ["同期", "synchronization", "sync"],
        ["消失", "消える", "消え", "失われ", "消えて"],
        ["戻り", "リセット", "英語に変更"],
        ["画像", "スクリーンショット"], ["復号", "復号化"],
        ["案件履歴", "案件記録"], ["非再発", "再発しなかった", "再発していない"],
        ["メーカー原文", "メーカー回答原文"], ["未確認", "確認できません", "確認されていない"],
        ["修正", "fixed"], ["チェッカー", "checker"], ["構成", "configuration"],
        ["既定", "デフォルト", "default"], ["変更", "change"],
        ["初回", "first"], ["前に", "前", "before"],
    ];

    public static ExpectedClaimAssessment Evaluate(IReadOnlyList<string> claims, string answer)
    {
        var normalizedAnswer = Normalize(answer);
        var elements = claims.SelectMany((claim, index) =>
            Regex.Split(claim, @"[。；;\r\n]+|、")
                .Where(static part => !string.IsNullOrWhiteSpace(part))
                .Select(part => Assess(index, part.Trim(), answer, normalizedAnswer))).ToArray();
        return new ExpectedClaimAssessment(elements);
    }

    private static ExpectedClaimElement Assess(int index, string text, string answer, string normalizedAnswer)
    {
        var requirements = new List<string[]>();
        requirements.AddRange(Regex.Matches(text,
                @"[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)+|[A-Za-z]*[A-Z][a-z]+[A-Z][A-Za-z]*|(?<![A-Za-z0-9])(?:[A-Z]{2,}|[A-Z][a-z]{2,})(?:\s+(?:[A-Z]{2,}|[A-Z][a-z]{2,}))*(?![A-Za-z0-9])|(?<!\d)\d+(?:\.\d+){1,3}(?!\d)|(?:約)?\d+週間")
            .Select(static match => new[] { match.Value }));
        requirements.AddRange(Concepts.Where(aliases => aliases.Any(alias =>
            Normalize(text).Contains(Normalize(alias), StringComparison.Ordinal))));
        if (requirements.Count == 0) requirements.Add([text]);
        var matched = requirements.Where(aliases => aliases.Any(alias =>
            normalizedAnswer.Contains(Normalize(alias), StringComparison.Ordinal))).ToArray();
        var missing = requirements.Except(matched).Select(static aliases => aliases[0]).ToArray();
        var excerpts = Regex.Split(answer, @"[。！？\r\n]+")
            .Where(sentence => matched.Any(aliases => aliases.Any(alias =>
                Normalize(sentence).Contains(Normalize(alias), StringComparison.Ordinal))))
            .Distinct().ToArray();
        return new ExpectedClaimElement(index, text, matched.Length > 0,
            missing.Length == 0, missing, string.Join("。", excerpts));
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"\s+", "");
}

public sealed record ExpectedClaimElement(int ClaimIndex, string Text, bool PartialMatch,
    bool Satisfied, IReadOnlyList<string> MissingRequirements, string AnswerExcerpt);

public sealed record ExpectedClaimAssessment(IReadOnlyList<ExpectedClaimElement> Elements)
{
    public double Coverage => Elements.Count == 0 ? 0 : (double)Elements.Count(element => element.Satisfied) / Elements.Count;
    public bool HasPartialMatch => Elements.Any(element => element.PartialMatch);
    public bool AllElementsSatisfied => Elements.Count > 0 && Elements.All(element => element.Satisfied);
}
