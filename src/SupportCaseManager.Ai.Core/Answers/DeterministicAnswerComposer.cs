using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Core.Answers;

/// <summary>Creates a conservative answer when an LLM result is unavailable.</summary>
public static class DeterministicAnswerComposer
{
    public static string ComposeHowTo(IReadOnlyList<EvidenceItem> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var builder = new StringBuilder();
        AppendEvidenceSection(builder, "【事前準備】", evidence, 0, 1);
        AppendEvidenceSection(builder, "【プロジェクト作成】", evidence, 1, 1);
        AppendEvidenceSection(builder, "【コンパイラ・CCT設定】", evidence, 2, 1);
        AppendEvidenceSection(builder, "【GUIでの手順】", evidence, 3, 1);
        AppendEvidenceSection(builder, "【CLIでの手順】", evidence, 4, 1);
        AppendEvidenceSection(builder, "【結果確認】", evidence, 5, 1);
        AppendEvidenceSection(builder, "【注意点】", evidence, 6, 1);
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

    private static void AppendEvidenceSection(StringBuilder builder, string heading, IReadOnlyList<EvidenceItem> evidence, int offset, int count)
    {
        builder.AppendLine(heading);
        var item = evidence.Where(static item => !string.IsNullOrWhiteSpace(item.Excerpt)).Skip(offset).Take(count).FirstOrDefault();
        builder.AppendLine(item is null ? "確認できません。" : $"・{Compact(item.Excerpt)}");
        builder.AppendLine();
    }

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
