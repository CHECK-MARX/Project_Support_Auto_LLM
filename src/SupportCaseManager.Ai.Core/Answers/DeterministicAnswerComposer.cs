using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Core.Answers;

/// <summary>Creates a conservative answer when an LLM result is unavailable.</summary>
public static class DeterministicAnswerComposer
{
    private static readonly (string Heading, string[] Terms)[] ProcedureSections =
    [
        ("【事前準備】", ["準備", "前提", "必要", "権限", "認証", "ライセンス", "環境", "prerequisite", "before"]),
        ("【プロジェクト作成】", ["プロジェクト", "作成", "開く", "関連付け", "project", "create", "open"]),
        ("【コンパイラ・CCT設定】", ["コンパイラ", "コンパイル", "CCT", "CIP", "include", "macro", "compiler"]),
        ("【GUIでの手順】", ["GUI", "画面", "メニュー", "クリック", "選択", "dashboard"]),
        ("【CLIでの手順】", ["qacli", "CLI", "コマンド", "command", "powershell", "shell"]),
        ("【結果確認】", ["確認", "検証", "結果", "完了", "done", "success", "status", "check", "verify"]),
        ("【注意点】", ["注意", "制限", "バージョン", "異なる", "warning", "注意事項"]),
    ];

    public static string ComposeHowTo(IReadOnlyList<EvidenceItem> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var builder = new StringBuilder();
        foreach (var (heading, terms) in ProcedureSections)
        {
            AppendEvidenceSection(builder, heading, evidence, terms);
        }
        builder.AppendLine("【参照先】");
        var references = evidence.Select(BuildReference).Where(static value => value.Length > 0).Distinct().ToList();
        builder.AppendLine(references.Count == 0 ? "確認できません。" : string.Join(Environment.NewLine, references.Select(static value => $"・{value}")));
        return builder.ToString().Trim();
    }

    public static string ComposeManufacturerConfirmation(IReadOnlyList<EvidenceItem> evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("【結論】");
        builder.AppendLine("選択された根拠だけでは、製品仕様を確定できません。メーカー公式情報で確認が必要です。");
        builder.AppendLine();
        builder.AppendLine("【確認できた内容】");
        foreach (var item in evidence.Where(static item => !string.IsNullOrWhiteSpace(item.Excerpt)).Take(3))
        {
            builder.AppendLine($"・{Compact(item.Excerpt)}");
        }
        builder.AppendLine();
        builder.AppendLine("【メーカー確認事項】");
        builder.AppendLine("・対象バージョン、適用条件、対応可否を公式資料で確認してください。");
        builder.AppendLine();
        builder.AppendLine("【参照先】");
        builder.AppendLine(string.Join(Environment.NewLine, evidence.Select(BuildReference).Where(static value => value.Length > 0).Distinct().Select(static value => $"・{value}")));
        return builder.ToString().Trim();
    }

    private static void AppendEvidenceSection(
        StringBuilder builder,
        string heading,
        IReadOnlyList<EvidenceItem> evidence,
        IReadOnlyList<string> terms)
    {
        builder.AppendLine(heading);
        var sentence = evidence
            .SelectMany(static item => SplitEvidence(item.Excerpt))
            .Select(value => (Value: value, Score: ScoreEvidence(value, terms)))
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Value.Length)
            .Select(static item => item.Value)
            .FirstOrDefault();
        builder.AppendLine(sentence is null ? "確認できません。" : $"・{Compact(sentence)}");
        builder.AppendLine();
    }

    private static IEnumerable<string> SplitEvidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var sentence in line.Split(['。', '！', '!', '？', '?', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = sentence.Trim();
                if (normalized.Length > 0)
                {
                    yield return normalized;
                }
            }
        }
    }

    private static int ScoreEvidence(string value, IReadOnlyList<string> terms) =>
        terms.Count(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string BuildReference(EvidenceItem item)
    {
        var title = item.DocumentTitle ?? item.Title;
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var value = title;
        if (item.PageNumber is > 0) value += $" Page {item.PageNumber.Value}";
        if (!string.IsNullOrWhiteSpace(item.SectionTitle)) value += $" 『{item.SectionTitle}』";
        return value;
    }

    private static string Compact(string value)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 500 ? compact : compact[..500] + "...";
    }
}
