using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Core.Compatibility;

internal static partial class CurrentCaseFactReader
{
    // Read only previously audited records. Reply drafts and evaluation labels
    // are deliberately outside this adapter.
    public static (string Inquiry, IReadOnlyList<ResolvedFact> Facts) ReadAudited(
        string caseFolder, string caseId, string fallbackInquiry, string auditFolder,
        string materialAuditPath, ISafetyRedactionService redactor)
    {
        var records = new List<SearchSource>();
        var inquiry = fallbackInquiry;
        var snapshot = Path.Combine(auditFolder, "source-notes-mail-private.json");
        if (File.Exists(snapshot))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshot));
            if (doc.RootElement.GetProperty("cases").TryGetProperty(caseId, out var entry))
            {
                foreach (var note in entry.GetProperty("notes").EnumerateArray())
                {
                    var kind = note.GetProperty("category").GetString();
                    if (kind is not ("question" or "manufacturer" or "other")) continue;
                    var path = Path.GetFullPath(note.GetProperty("path").GetString()!);
                    if (!path.StartsWith(Path.GetFullPath(caseFolder) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                    var bytes = File.ReadAllBytes(path);
                    if (Sha256(bytes) != note.GetProperty("sha256").GetString()) continue;
                    var text = EncodingPolicy.DecodeNoteText(bytes);
                    if (kind == "question")
                    {
                        var technical = TechnicalInquiry(text, redactor);
                        if (technical.Length > inquiry.Length && technical.Length <= 2800) inquiry = technical;
                    }
                    else records.Add(Record(text, Sha256(bytes), "監査済み案件履歴", "CaseHistory"));
                }
                // The entry audit binds an actual manufacturer email by body
                // hash. Do not use H/I/J, reply drafts, or inferred sender roles.
                var ledger = Directory.EnumerateFiles(Path.GetDirectoryName(auditFolder)!, "*entry-latest-*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (ledger is not null && entry.TryGetProperty("mail", out var mail))
                {
                    using var audit = JsonDocument.Parse(File.ReadAllText(ledger));
                    var reviewed = audit.RootElement.GetProperty("cases").EnumerateArray()
                        .FirstOrDefault(c => c.GetProperty("caseId").GetString() == caseId);
                    if (reviewed.ValueKind == JsonValueKind.Object &&
                        reviewed.TryGetProperty("resolutionSource", out var binding) &&
                        binding.TryGetProperty("bodySha256", out var verifiedHash))
                        foreach (var message in mail.EnumerateArray())
                        {
                            var body = message.GetProperty("body").GetString() ?? "";
                            var hash = Sha256(Encoding.UTF8.GetBytes(body));
                            if (hash != verifiedHash.GetString()) continue;
                            var current = Regex.Split(body, @"(?im)^\s*(?:From:|差出人:|On .+wrote:)")[0];
                            records.Add(Record(current, hash, "監査済みメーカー原メールの案件記録", "ManufacturerRecord"));
                        }
                }
            }
        }
        if (File.Exists(materialAuditPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(materialAuditPath));
            if (doc.RootElement.GetProperty("cases").TryGetProperty(caseId, out var entry))
            {
                // A visual/text finding is usable only when its recorded file
                // fingerprint still matches an actual attachment in this case.
                var files = Directory.EnumerateFiles(caseFolder, "*", SearchOption.AllDirectories).ToArray();
                foreach (var finding in entry.GetProperty("files").EnumerateArray())
                {
                    var status = finding.GetProperty("status").GetString();
                    if (status is not ("image_visual_reviewed" or "binary_content_reviewed" or "text_scanned" or "archive_content_reviewed")) continue;
                    var prefix = finding.GetProperty("sha256prefix").GetString()!;
                    var size = finding.GetProperty("bytes").GetInt64();
                    var matches = files.Where(f => new FileInfo(f).Length == size &&
                        Path.GetExtension(f).Equals(finding.GetProperty("extension").GetString(), StringComparison.OrdinalIgnoreCase))
                        .Select(f => (Path: f, Hash: Sha256(File.ReadAllBytes(f))))
                        .Where(f => f.Hash.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1) continue;
                    var value = finding.TryGetProperty("contentFinding", out var content) ? content.GetString() :
                        finding.TryGetProperty("visualFinding", out var visual) ? visual.GetString() : null;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    records.Add(Record(value, matches[0].Hash, "監査済み添付の観測", "AuditedAttachment"));
                }
            }
        }
        return (inquiry, CurrentCaseFactProjector.Project(inquiry, records, redactor));
    }

    private static SearchSource Record(string text, string fileHash, string title, string kind) => new()
    {
        SourceId = "case-audit:" + fileHash, SourceType = "CurrentCase", ParseStatus = "AUDITED",
        Text = text, ContentHash = Sha256(Encoding.UTF8.GetBytes(text)), Title = title, EvidenceKind = kind,
    };

    private static string TechnicalInquiry(string text, ISafetyRedactionService redactor) =>
        string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => l.Trim())
            .Where(l => l.Length is >= 8 and <= 400 && !PrivateLine().IsMatch(l) &&
                !l.StartsWith('>') &&
                !Regex.IsMatch(l, @"CONFIDENTIAL|お世話にな|よろしく|ありがとうございます|^[-=*]{3,}", RegexOptions.IgnoreCase))
            .Where(l => TechnicalTerms(l).Count > 0).Select(redactor.RedactForCloud).Distinct(StringComparer.Ordinal));

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
                    Key = PreviousOutcome().IsMatch(line) ? "PriorCaseOutcome" : "CaseObservation",
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
