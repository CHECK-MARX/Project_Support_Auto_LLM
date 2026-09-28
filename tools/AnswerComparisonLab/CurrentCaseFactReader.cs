using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Core.Compatibility;

internal static partial class CurrentCaseFactReader
{
    public static IReadOnlyList<ResolvedFact> Read(string caseFolder, string inquiry,
        ISafetyRedactionService redactor)
    {
        var facts = new List<ResolvedFact>();
        var terms = TechnicalTerms(inquiry);
        foreach (var path in Directory.EnumerateFiles(caseFolder, "*連携内容*.txt")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(path);
            var lines = EncodingPolicy.DecodeNoteText(bytes)
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            var candidates = lines.Select((raw, index) =>
            {
                var line = raw.Trim();
                return (Line: line, Index: index + 1,
                    Score: Score(line, terms));
            })
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Take(2);

            var fileHash = Sha256(bytes);
            foreach (var (line, index, _) in candidates)
            {
                var safe = redactor.RedactForCloud(line);
                facts.Add(new ResolvedFact
                {
                    FactId = $"case:{fileHash[..16]}:{index}",
                    Key = "CaseObservation",
                    Statement = "案件履歴に記録された観測。製品仕様・メーカー原文の確定ではない。",
                    Value = safe,
                    Status = FactStatuses.Candidate,
                    Confidence = FactConfidences.Medium,
                    SourceType = "CurrentCase",
                    EvidenceId = $"case:{fileHash[..16]}:line:{index}:sha256:{Sha256(Encoding.UTF8.GetBytes(line))[..16]}",
                    DocumentTitle = "案件履歴",
                    Section = $"line:{index}",
                    AuthorityLevel = "CaseRecord",
                });
            }
        }

        facts.AddRange(ReadLogObservations(caseFolder, redactor));

        var attachmentCounts = Directory.EnumerateFiles(caseFolder, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("お客様ご相談内容", StringComparison.Ordinal) &&
                !Path.GetFileName(path).StartsWith("お客様への返信案", StringComparison.Ordinal) &&
                !Path.GetFileName(path).Contains("連携内容", StringComparison.Ordinal))
            .GroupBy(static path => Path.GetExtension(path).ToLowerInvariant())
            .Where(static group => group.Key is ".png" or ".jpg" or ".jpeg" or ".xml" or
                ".log" or ".zip" or ".xlsx" or ".pdf")
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => $"{group.Key}:{group.Count()}")
            .ToArray();
        if (attachmentCounts.Length > 0)
        {
            facts.Add(new ResolvedFact
            {
                FactId = "case:attachment-inventory",
                Key = "CaseAttachmentInventory",
                Statement = "添付形式と件数のみ確認。内容は未確認。",
                Value = string.Join(", ", attachmentCounts),
                Status = FactStatuses.Candidate,
                Confidence = FactConfidences.Low,
                SourceType = "CurrentCase",
                EvidenceId = "case:attachment-inventory",
                AuthorityLevel = "CaseRecord",
            });
        }
        return facts;
    }

    private static IReadOnlyList<ResolvedFact> ReadLogObservations(
        string caseFolder, ISafetyRedactionService redactor)
    {
        var observations = new List<(int Score, ResolvedFact Fact)>();
        foreach (var path in Directory.EnumerateFiles(caseFolder, "*.log", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).Take(40))
        {
            var file = new FileInfo(path);
            if (file.Length is 0 or > 2_000_000) continue;
            var bytes = File.ReadAllBytes(path);
            var lines = EncodingPolicy.DecodeNoteText(bytes)
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            var fileHash = Sha256(bytes);
            for (var index = 0; index < Math.Min(lines.Length, 2000); index++)
            {
                var line = lines[index];
                if (!LogFailure().IsMatch(line)) continue;
                var component = LogComponent().Match(line).Value;
                var operation = LogMq().IsMatch(line) ? "MQ接続" :
                    LogConnection().IsMatch(line) ? "接続" : "処理";
                var observation = string.IsNullOrEmpty(component)
                    ? $"添付ログに{operation}の失敗が記録されている。"
                    : $"添付ログに{component}の{operation}失敗が記録されている。";
                observations.Add((LogMq().IsMatch(line) ? 10 : LogConnection().IsMatch(line) ? 5 : 1,
                    new ResolvedFact
                    {
                        FactId = $"case-log:{fileHash[..16]}:{index + 1}",
                        Key = "ObservedLogFailure",
                        Statement = "添付ログの観測。製品仕様や根本原因の確定ではない。",
                        Value = redactor.RedactForCloud(observation),
                        Status = FactStatuses.Candidate,
                        Confidence = FactConfidences.Low,
                        SourceType = "CurrentCase",
                        EvidenceId = $"case-log:{fileHash[..16]}:line:{index + 1}:sha256:{Sha256(Encoding.UTF8.GetBytes(line))[..16]}",
                        DocumentTitle = "添付ログ",
                        Section = $"line:{index + 1}",
                        AuthorityLevel = "CaseRecord",
                    }));
            }
        }
        return observations.OrderByDescending(static item => item.Score)
            .Select(static item => item.Fact)
            .DistinctBy(static fact => fact.Value, StringComparer.Ordinal)
            .Take(2).ToArray();
    }

    private static int Score(string line, IReadOnlyList<string> terms)
    {
        if (line.Length is < 24 or > 240 || PrivateLine().IsMatch(line) ||
            SecondaryDocumentClaim().IsMatch(line) ||
            Hypothesis().IsMatch(line) || QuestionLine().IsMatch(line) ||
            !ObservedOutcome().IsMatch(line))
            return 0;
        var overlap = terms.Count(term => line.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (overlap == 0 && !CaseEvent().IsMatch(line)) return 0;
        return overlap * 3 + (CaseEvent().IsMatch(line) ? 2 : 0) +
            (ObservedOutcome().IsMatch(line) ? 9 : 0) +
            (PreviousOutcome().IsMatch(line) ? 20 : 0) +
            (VersionOrDuration().IsMatch(line) ? 1 : 0);
    }

    private static IReadOnlyList<string> TechnicalTerms(string inquiry) =>
        TechnicalToken().Matches(inquiry).Select(static match => match.Value)
            .Where(static term => term.Length >= 3 && !GenericTerms.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();

    private static readonly HashSet<string> GenericTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "について", "でしょうか", "ください", "お願い", "確認", "現在", "場合", "情報",
        "customer", "support", "subject", "hello", "thank", "please",
    };

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_.-]{2,}|[ァ-ヴー]{3,}|[一-龠]{2,}")]
    private static partial Regex TechnicalToken();

    [GeneratedRegex(@"株式会社|有限会社|合同会社|御中|[一-龠]+(?:[A-Za-z0-9-]+)?様|(?:^|\s)(?:To|From|Cc|Bcc|Subject)\s*[:：]|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|https?://|(?:^|[\s　])[^\s　]+様(?:[\s　、。,，．:：]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateLine();

    [GeneratedRegex(@"(?:suspect|possible|hypothesis|may include|might|推測|仮説|可能性|疑い|想定される条件)", RegexOptions.IgnoreCase)]
    private static partial Regex Hypothesis();

    [GeneratedRegex(@"(?:release notes|what['’]?s new|新機能ページ|公式資料|マニュアル).{0,180}(?:states|confirmed that|記載|確認した)", RegexOptions.IgnoreCase)]
    private static partial Regex SecondaryDocumentClaim();

    [GeneratedRegex(@"(?:[?？]|(?:ある|ない|どう|何)か[。．]?$)")]
    private static partial Regex QuestionLine();

    [GeneratedRegex(@"(?:actual behavior|did not occur|closed as resolved|changed the|temporary workaround|confirmed that|completed successfully|were stopped|after starting|実際の動作|一時回避|解消した|再発しなかった|変更した|確認した|修正された|回答がございました)", RegexOptions.IgnoreCase)]
    private static partial Regex ObservedOutcome();

    [GeneratedRegex(@"(?:after that|did not occur|closed as resolved|その後|再発しなかった|改善した)", RegexOptions.IgnoreCase)]
    private static partial Regex PreviousOutcome();

    [GeneratedRegex(@"(?:previous case|one week|manufacturer|service|scan|started|stopped|completed|メーカー|修正|改善|再発|設定|言語|同期|ログ|添付|チェッカー|確認|version|release|fixed|resolved|issue|sync|language|user data location)", RegexOptions.IgnoreCase)]
    private static partial Regex CaseEvent();

    [GeneratedRegex(@"(?:failed|failure|error|unable|refused|timeout|接続失敗|失敗|エラー)", RegexOptions.IgnoreCase)]
    private static partial Regex LogFailure();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?:[A-Z][A-Za-z0-9]+)+\b")]
    private static partial Regex LogComponent();

    [GeneratedRegex(@"(?:\bMQ\b|queue|ActiveMQ)", RegexOptions.IgnoreCase)]
    private static partial Regex LogMq();

    [GeneratedRegex(@"(?:connect|connection|接続)", RegexOptions.IgnoreCase)]
    private static partial Regex LogConnection();

    [GeneratedRegex(@"\b\d{4}\.\d+(?:\.\d+)*\b|\b\d+(?:\s|-)?week\b|週間|バージョン|version", RegexOptions.IgnoreCase)]
    private static partial Regex VersionOrDuration();
}
