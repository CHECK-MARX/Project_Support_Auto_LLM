using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Core.Facts;

/// <summary>Projects verified case records, not product specification evidence.</summary>
public static class CurrentCaseFactProjector
{
    public static IReadOnlyList<ResolvedFact> Project(string inquiry,
        IReadOnlyList<SearchSource> records, ISafetyRedactionService redactor)
    {
        var terms = Regex.Matches(inquiry, @"[A-Za-z][A-Za-z0-9_.-]{2,}|[ァ-ヴー]{3,}|[一-龠]{2,}")
            .Select(m => m.Value).Where(t => t is not ("について" or "お願い" or "確認" or "ください"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var facts = new List<ResolvedFact>();
        foreach (var record in records.Where(r => r.SourceType == "CurrentCase" &&
                     r.ParseStatus == "AUDITED" && !string.IsNullOrWhiteSpace(r.SourceId)))
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(record.Text)));
            if (!string.Equals(hash, record.ContentHash, StringComparison.OrdinalIgnoreCase)) continue;
            var spans = Regex.Matches(record.Text, @"[^\r\n。!?]+(?:[。!?]|$)")
                .Where(m => m.Length is >= 12 and <= 320)
                .Where(m => !Regex.IsMatch(m.Value, @"^\s*(?:From|To|Cc|Subject|差出人|宛先)\s*[:：]|株式会社|御中|様|https?://|CONFIDENTIAL", RegexOptions.IgnoreCase))
                .Where(m => !Regex.IsMatch(m.Value, @"仮説|推測|可能性|might|hypothesis", RegexOptions.IgnoreCase))
                .Select(m => new { Span = m, Score = terms.Count(t => m.Value.Contains(t, StringComparison.OrdinalIgnoreCase)) +
                    (Regex.IsMatch(m.Value, @"版|Version|\d+\.\d+|変更|設定|復旧|添付|依頼|検討|観測|試験|未確認|support|tested|before", RegexOptions.IgnoreCase) ? 2 : 0) })
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Span.Index).Take(2);
            foreach (var item in spans)
            {
                var span = item.Span; var value = redactor.RedactForCloud(span.Value.Trim());
                if (string.IsNullOrWhiteSpace(value)) continue;
                facts.Add(new ResolvedFact
                {
                    FactId = $"case-record:{record.SourceId}:{span.Index}",
                    Key = "AuditedCaseFact", Value = value,
                    Statement = record.EvidenceKind == "AuditedAttachment"
                        ? "監査済み添付に記録された内容。製品仕様や根本原因の証明ではない。"
                        : "監査済み案件記録の記載。顧客申告・計画・履歴という帰属を保持し、公式仕様へ昇格しない。",
                    Status = FactStatuses.Candidate, Confidence = FactConfidences.Medium,
                    SourceType = "CurrentCase", AuthorityLevel = "CaseRecord",
                    EvidenceId = record.SourceId, DocumentTitle = record.Title,
                    Section = $"sha256:{hash};span:{span.Index}:{span.Length}",
                    Explanation = record.EvidenceKind ?? "CaseHistory",
                });
            }
        }
        return facts.DistinctBy(f => (f.EvidenceId, f.Value)).Take(6).ToArray();
    }
}
