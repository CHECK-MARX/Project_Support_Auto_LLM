using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Core.Facts;

/// <summary>Projects only directly relevant, verbatim official statements from selected evidence.</summary>
public static class SelectedOfficialFactProjector
{
    private static readonly string[][] ConceptAliases =
    [
        ["checker", "checkers", "チェッカー"],
        ["configuration", "configure", "構成", "設定"],
        ["migrate", "migration", "upgrade", "移行", "バージョンアップ"],
        ["Azure"],
        ["Managed Instance", "マネージドインスタンス"],
        ["SQL", "データベース"],
        ["license", "ライセンス"],
        ["scan", "スキャン"],
    ];

    private static readonly string[][] DistinctTopicAliases =
    [
        ["language", "言語", "英語", "日本語"],
        ["synchronization", "synchronisation", "sync", "同期"],
        ["checker", "checkers", "チェッカー"],
        ["Azure"],
        ["Managed Instance", "マネージドインスタンス"],
        ["SQL"],
        ["license", "ライセンス"],
        ["scan", "スキャン"],
        ["user data", "ユーザーデータ"],
        ["enabled", "disabled", "有効", "無効", "既定"],
    ];

    public static bool HasDirectTopicOverlap(string inquiry, SearchSource source)
    {
        var concepts = DistinctTopicAliases.Where(aliases => ContainsAny(inquiry, aliases)).ToArray();
        return concepts.Length < 2 || concepts.Any(aliases =>
            ContainsAny($"{source.Title} {source.SectionTitle} {source.Text}", aliases));
    }

    public static IReadOnlyList<ResolvedFact> Project(string inquiry,
        IReadOnlyList<SearchSource> selectedSources)
    {
        var inquiryConcepts = ConceptAliases.Where(aliases => ContainsAny(inquiry, aliases)).ToArray();
        var redactor = new SafetyRedactionService();
        var facts = new List<ResolvedFact>();
        foreach (var source in selectedSources.Where(source =>
            source.SourceType == "OfficialDoc" &&
            Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            var statement = Regex.Split(source.Text, @"(?<=[。.!?])\s+")
                .Select(static sentence => sentence.Trim())
                .Where(static sentence => sentence.Length is >= 35 and <= 500)
                .Where(static sentence => Regex.IsMatch(sentence,
                    @"\b(?:verify|supported|requires?|must|should|confirm|check)\b|確認|必要|対応",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(sentence => new
                {
                    Text = sentence,
                    Matches = inquiryConcepts.Count(aliases => ContainsAny(sentence, aliases)),
                })
                .Where(static item => item.Matches >= 2)
                .OrderByDescending(static item => item.Matches)
                .ThenBy(static item => item.Text.Length)
                .Select(static item => item.Text)
                .FirstOrDefault();
            if (statement is null || !string.Equals(redactor.RedactForCloud(statement), statement,
                    StringComparison.Ordinal))
                continue;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(statement)))
                .ToLowerInvariant()[..16];
            var conditional = Regex.Match(statement,
                @"\bIf\s+(?<condition>[^,]{8,180}),\s*(?<action>(?:verify|check|confirm|ensure|use|run)\b[^.]{8,240})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var heading = conditional.Success ? Regex.Match(statement,
                @"(?<heading>[A-Z][a-z]+(?:\s+[a-z]+){1,3})\s+If\s+",
                RegexOptions.CultureInvariant) : Match.Empty;
            facts.Add(new ResolvedFact
            {
                FactId = $"selected-official:{hash}",
                Key = "SelectedOfficialStatement",
                Statement = conditional.Success
                    ? (heading.Success ? $"選択した公式資料に見出し『{heading.Groups["heading"].Value}』が存在する。" : string.Empty) +
                      $"公式資料の条件付き指示。条件: {conditional.Groups["condition"].Value.Trim()}; " +
                      $"必要な対応: {conditional.Groups["action"].Value.Trim()}。" +
                      "条件外の仕様や既定値の変更有無は証明しない。"
                    : "選択した公式資料の原文記述。条件・対象版を維持し、未記載の既定値や個別環境へ拡張しない。",
                Value = statement,
                Status = FactStatuses.Confirmed,
                Confidence = FactConfidences.High,
                SourceType = "OfficialDoc",
                EvidenceId = source.SourceId,
                SourceUrls = [source.Url!],
                DocumentTitle = source.Title,
                Section = source.SectionTitle ?? string.Empty,
                AuthorityLevel = "OfficialDoc",
            });
        }
        return facts.DistinctBy(static fact => fact.Value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> aliases) =>
        aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase));

    public static bool SharesFactSubject(string statement, string text)
    {
        var concepts = ConceptAliases.Where(aliases => ContainsAny(statement, aliases)).ToArray();
        return concepts.Length >= 2 && concepts.Count(aliases => ContainsAny(text, aliases)) >= 2;
    }
}
