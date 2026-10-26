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
        ["manual", "マニュアル"],
        ["report", "レポート"],
        ["suppress", "suppression", "抑止"],
        ["error", "エラー"],
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
        foreach (var source in selectedSources.Where(source => source.SourceType == "Manual" ||
            source.SourceType == "OfficialDoc" &&
            Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            var exactTerms = ExactSubjectTerms(inquiry);
            var subjectIdentifiers = exactTerms.Where(t => t.Any(c => c is '_' or '-') ||
                t.All(c => !char.IsLetter(c) || char.IsUpper(c)) &&
                !new[] { "GUI", "CLI", "XML", "CSV", "PDF", "HTTP", "HTTPS", "JSON", "API" }.Contains(t, StringComparer.Ordinal) ||
                Regex.IsMatch(inquiry, Regex.Escape(t) + @"\s+[a-z][a-z_-]+"))
                .ToArray();
            if (subjectIdentifiers.Length > 0 && !subjectIdentifiers.Any(t =>
                    source.Text.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            var statement = VerbatimSpans(source.Text)
                .Select(static sentence => sentence.Trim())
                .Where(static sentence => sentence.Length is >= 20 and <= 500)
                .Where(static sentence => Regex.IsMatch(sentence,
                    @"\b(?:verify|supported|requires?|must|should|confirm|check|support|report)\b|確認|必要|対応|抑止|マニュアル|例示|サポート外",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(sentence => new
                {
                    Text = sentence,
                    Matches = inquiryConcepts.Count(aliases => ContainsAny(sentence, aliases)),
                })
                .Where(item => item.Matches >= 2 || exactTerms.Any(term =>
                    item.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
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
                SourceType = source.SourceType,
                EvidenceId = source.SourceId,
                SourceUrls = source.Url is null ? [] : [source.Url],
                DocumentTitle = source.Title,
                Section = source.SectionTitle ?? string.Empty,
                AuthorityLevel = source.SourceType,
            });
        }
        return facts.Select(f => f.SourceType == "Manual" ? f with
            { Statement = "選択したManualの原文記録。公式性を断定せず、対象版・条件・時点を保持する。" } : f)
            .DistinctBy(static fact => (fact.SourceType, fact.EvidenceId, fact.Value)).ToArray();
    }

    // Substrings of the original source, never concatenated statements. A nearby
    // condition/heading may be included only as part of one contiguous span.
    internal static IEnumerable<string> VerbatimSpans(string text)
    {
        foreach (Match match in Regex.Matches(text, @"[^\r\n]+"))
        {
            foreach (var sentence in Regex.Split(match.Value, @"(?<=[。!?])\s*|(?<=\.)(?!\d)\s+"))
                if (!string.IsNullOrWhiteSpace(sentence)) yield return sentence.Trim();
            var end = match.Index + match.Length;
            var next = Regex.Match(text[end..], @"^[\r\n]+[^\r\n]+");
            if (next.Success && match.Length + next.Length <= 500 &&
                Regex.IsMatch(match.Value, @"\b(?:If|When|Before|Note)\b|条件|注意|例示|サポート外", RegexOptions.IgnoreCase))
                yield return text.Substring(match.Index, match.Length + next.Length).Trim();
        }
    }

    internal static IReadOnlyList<string> ExactSubjectTerms(string inquiry) => Regex.Matches(inquiry,
            @"(?<![A-Za-z0-9_.-])[A-Za-z][A-Za-z0-9_.-]{2,}(?![A-Za-z0-9_.-])")
        .Select(m => m.Value).Where(t => t is not ("QAC" or "CxSAST" or "Checkmarx" or "Klocwork" or "Windows" or "Version" or "Server" or "SQL" or "HF"))
        .Where(t => t.Any(c => c is '_' or '-' or '.') || t.All(c => !char.IsLetter(c) || char.IsUpper(c)) ||
            t.All(c => !char.IsLetter(c) || char.IsLower(c)) &&
            !new[] { "server", "manual", "version", "report", "license", "configuration", "check", "verify", "scan", "build", "integration", "first", "support", "before", "after" }.Contains(t, StringComparer.OrdinalIgnoreCase))
        .Where(t => !t.Contains("REDACTED", StringComparison.OrdinalIgnoreCase) && !t.Contains("CUSTOMER", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<SearchSource> SelectRelevantEvidence(string inquiry,
        IEnumerable<SearchSource> ranked, int count)
    {
        var candidates = ranked.Take(50).Where(s => HasDirectTopicOverlap(inquiry, s))
            .Select((s, rank) => new { Source = s, Rank = rank, Fact = Project(inquiry, [s]).FirstOrDefault() })
            .OrderByDescending(x => x.Fact is not null).ThenBy(x => x.Rank);
        var chosen = new List<SearchSource>();
        foreach (var candidate in candidates)
        {
            if (chosen.Any(s => s.SourceId == candidate.Source.SourceId ||
                    string.Equals(s.Text, candidate.Source.Text, StringComparison.Ordinal))) continue;
            chosen.Add(candidate.Source);
            if (chosen.Count == count) break;
        }
        return chosen;
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> aliases) =>
        aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase));

    public static bool SharesFactSubject(string statement, string text)
    {
        var concepts = ConceptAliases.Where(aliases => ContainsAny(statement, aliases)).ToArray();
        return concepts.Length >= 2 && concepts.Count(aliases => ContainsAny(text, aliases)) >= 2;
    }
}
