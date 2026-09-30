using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Core.Facts;

/// <summary>
/// Keeps high-signal inquiry and case observations in the existing FactResolution.
/// These facts describe reports and case history, never product specifications.
/// </summary>
internal static class ImportantFactContract
{
    private static readonly Regex StartupFailure = new(
        @"(?<entity>(?:[A-Za-z][A-Za-z0-9._-]{1,50}|[一-龥ァ-ヴー][一-龥ァ-ヴー0-9._-]{1,50})(?:サーバー|サービス|プロセス))\s*(?:を|が|は)?\s*(?<polarity>起動できません|起動できない|起動に失敗|起動しませんでした|起動せず)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Migration = new(
        @"(?<from>[\p{L}A-Za-z0-9 _-]{2,60})から[、,\s]*(?<to>[\p{L}A-Za-z0-9 _-]{2,60})へ(?:の)?(?<operation>移設|移行)(?<plan>を検討|を計画|予定)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ServiceRecovery = new(
        @"(?<first>[A-Za-z][A-Za-z0-9]+)\s+and\s+(?<second>[A-Za-z][A-Za-z0-9]+)\s+were\s+stopped",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Version = new(@"(?<!\d)\d{1,4}(?:\.\d+){1,3}(?!\d)");

    internal static FactResolutionResult Enrich(FactResolutionResult resolution, string inquiry,
        InquiryFocus? focus, ISafetyRedactionService redactor)
    {
        var additions = new List<ResolvedFact>();
        foreach (var failure in StartupFailure.Matches(inquiry)
            .Cast<Match>().DistinctBy(match => match.Groups["entity"].Value,
                StringComparer.OrdinalIgnoreCase))
            additions.Add(InquiryFact("ImportantInquiryFailure", failure.Value.Trim(), "起動", redactor));
        foreach (Match migration in Migration.Matches(inquiry))
            additions.Add(InquiryFact("ImportantInquiryOperation", migration.Value.Trim(),
                migration.Groups["operation"].Value, redactor));
        foreach (var version in focus?.TargetVersions ?? [])
        {
            if (!inquiry.Contains(version, StringComparison.Ordinal)) continue;
            var qualified = QualifyInquiryVersion(inquiry, version);
            additions.Add(InquiryFact("ImportantInquiryVersion", qualified, "対象Version", redactor));
        }
        return additions.Count == 0 ? resolution : resolution with
        {
            ResolvedFacts = resolution.ResolvedFacts.Concat(additions)
                .DistinctBy(static fact => (fact.SourceType, fact.Key, fact.Value)).ToArray(),
        };
    }

    internal static IReadOnlyList<ResolvedFact> Select(FactResolutionResult? resolution) =>
        resolution?.ResolvedFacts.Where(IsImportant).ToArray() ?? [];

    internal static string QualifyInquiryVersion(string inquiry, string version)
    {
        var hotfix = Regex.Match(inquiry,
            Regex.Escape(version) + @"\s*(?<hotfix>(?:HF|Hotfix|ホットフィックス)\s*\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return hotfix.Success ? $"{version} {hotfix.Groups["hotfix"].Value}" : version;
    }

    internal static string Describe(ResolvedFact fact)
    {
        var polarity = fact.Key switch
        {
            "ImportantInquiryFailure" or "ObservedLogFailure" => "失敗",
            "ImportantInquiryOperation" => "計画・可否未確認",
            "ImportantInquiryVersion" => "顧客が明示した対象版",
            "PriorCaseOutcome" => "当時の非再発・復旧観測",
            "CaseObservation" when IsServiceRecovery(fact.Value) => "停止→起動後に完了",
            "CaseObservation" when IsVersionedFix(fact.Value) => "案件履歴に修正の記録・メーカー原文未確認",
            _ => "未分類",
        };
        var versions = Version.Matches(fact.Value).Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        var operation = fact.Operation.Length > 0 ? fact.Operation :
            fact.Key == "ObservedLogFailure" ? "ログ中の接続" :
            IsServiceRecovery(fact.Value) ? "サービス起動後のスキャン" :
            IsVersionedFix(fact.Value) ? "不具合修正" :
            fact.Key == "PriorCaseOutcome" ? "過去の設定変更" : "観測";
        var duration = Regex.Match(fact.Value, @"(?:約)?\d+(?:週間|か月|日)");
        var timing = duration.Success ? duration.Value :
            IsServiceRecovery(fact.Value) ? "両サービス起動後" : "原文に明記なし";
        return $"重要Fact sourceType={fact.SourceType}; polarity={polarity}; " +
            $"version={(versions.Length == 0 ? "原文に明記なし" : string.Join(",", versions))}; " +
            $"operation={operation}; timing={timing}; sourceId={fact.EvidenceId}; " +
            $"value={fact.Value}";
    }

    internal static string DescribeForGeneration(ResolvedFact fact)
    {
        var source = fact.SourceType == "CurrentInquiry" ? "顧客申告" :
            fact.Key == "CaseObservation" && IsVersionedFix(fact.Value)
                ? "メーカー原文未確認のCurrentCase案件履歴"
                : "CurrentCase案件履歴・添付の観測";
        var meaning = fact.Key switch
        {
            "ImportantInquiryFailure" when StartupFailure.Match(fact.Value) is { Success: true } failure =>
                $"{failure.Groups["entity"].Value}は起動できない。起動を試みたことを起動成功と書かない",
            "ImportantInquiryVersion" => $"問い合わせで明示された対象版は{fact.Value}。資料や案件履歴の版に帰属させない",
            "CaseObservation" when IsServiceRecovery(fact.Value) =>
                DescribeRecovery(fact.Value),
            "CaseObservation" when IsVersionedFix(fact.Value) =>
                $"{Version.Match(fact.Value).Value}で不具合修正とのメーカー回答があったという案件記録。メーカー原文は未確認",
            _ => fact.Value,
        };
        return $"{Describe(fact).Split("; value=", 2, StringSplitOptions.None)[0]}; " +
            $"attribution={source}; customerMeaning={meaning}";
    }

    private static string DescribeRecovery(string value)
    {
        var recovery = ServiceRecovery.Match(value);
        return $"{recovery.Groups["first"].Value}と{recovery.Groups["second"].Value}が停止し、" +
            "両サービスの起動後にスキャン完了と案件履歴に記録。根本原因の断定ではない";
    }

    internal static string? Validate(FactResolutionResult? resolution, string reply)
    {
        var factSection = reply.Split("現時点で断定できない事項", 2,
            StringSplitOptions.None)[0];
        foreach (var fact in Select(resolution))
        {
            if (fact.Key == "ImportantInquiryVersion")
            {
                if (!reply.Contains(fact.Value, StringComparison.OrdinalIgnoreCase))
                    return $"重要Factの欠落: 問い合わせ対象版 {fact.Value} が顧客向け本文にありません。";
            }
            else if (fact.Key == "ImportantInquiryFailure")
            {
                var failure = StartupFailure.Match(fact.Value);
                if (!failure.Success) continue;
                var entity = failure.Groups["entity"].Value;
                var mention = factSection.IndexOf(entity, StringComparison.OrdinalIgnoreCase);
                if (mention < 0) return "重要Factの欠落: 起動失敗の対象が回答本文にありません。";
                var near = factSection.Substring(mention, Math.Min(85, factSection.Length - mention));
                if (Regex.IsMatch(near, @"起動し(?:た|ている|ています|、)|起動中|稼働中|正常に起動") &&
                    !Regex.IsMatch(near, @"起動できません|起動できな|起動に失敗|起動せず|起動しな"))
                    return "重要Factの極性反転: 起動失敗を起動済みとして記載しています。";
                if (!Regex.IsMatch(near, @"起動できません|起動できな|起動に失敗|起動せず|起動しな|起動失敗"))
                    return "重要Factの欠落: 起動失敗が回答本文にありません。";
            }
            else if (fact.Key == "ImportantInquiryOperation")
            {
                var migration = Migration.Match(fact.Value);
                if (!migration.Success) continue;
                if (!Regex.IsMatch(factSection, @"移設|移行") ||
                    !ContainsAnchors(factSection, migration.Groups["from"].Value) ||
                    !ContainsAnchors(factSection, migration.Groups["to"].Value))
                    return "重要Factの欠落: 移設元・移設先・操作の関係が回答本文にありません。";
                if (migration.Groups["plan"].Success &&
                    !Regex.IsMatch(factSection, @"検討|計画|予定"))
                    return "重要Factの極性反転: 検討中の移設を実施済みとして扱っています。";
            }
            else if (fact.Key == "PriorCaseOutcome")
            {
                if (!IsPriorCaseOutcomePreserved(fact, reply))
                    return "重要Factの欠落: 過去の非再発・復旧観測が回答本文にありません。";
            }
            else if (fact.Key == "CaseObservation" && IsServiceRecovery(fact.Value))
            {
                var recovery = ServiceRecovery.Match(fact.Value);
                var first = factSection.IndexOf(recovery.Groups["first"].Value,
                    StringComparison.OrdinalIgnoreCase);
                var second = factSection.IndexOf(recovery.Groups["second"].Value,
                    StringComparison.OrdinalIgnoreCase);
                if (first < 0 || second < 0)
                    return "重要Factの欠落: 停止したサービスと起動後の復旧履歴が回答本文にありません。";
                var sequence = factSection[first..Math.Min(factSection.Length, first + 250)];
                var stop = Regex.Match(sequence, @"停止|stopped", RegexOptions.IgnoreCase);
                var start = Regex.Match(sequence, @"起動|starting|started", RegexOptions.IgnoreCase);
                var complete = Regex.Match(sequence, @"(?:スキャン|scan).{0,24}(?:完了|completed)",
                    RegexOptions.IgnoreCase);
                if (second > first + 160 || !stop.Success || !start.Success || !complete.Success ||
                    !(stop.Index < start.Index && start.Index < complete.Index) ||
                    !factSection.Contains("案件履歴", StringComparison.Ordinal))
                    return "重要Factの欠落: 停止したサービスと起動後の復旧履歴が回答本文にありません。";
            }
            else if (fact.Key == "CaseObservation" && IsVersionedFix(fact.Value))
            {
                var version = Version.Match(fact.Value).Value;
                if (!factSection.Contains(version, StringComparison.Ordinal) ||
                    !Regex.IsMatch(factSection, @"修正|fix(?:ed)?", RegexOptions.IgnoreCase) ||
                    !factSection.Contains("案件履歴", StringComparison.Ordinal))
                    return "重要Factの欠落: 版付き修正記録の内容または案件履歴への帰属がありません。";
                if (Regex.IsMatch(factSection,
                    Regex.Escape(version) + @".{0,50}修正(?:されていない|されず|なし|未実施)"))
                    return "重要Factの極性反転: 案件履歴の版付き修正記録を否定しています。";
            }
            else if (fact.Key == "ObservedLogFailure")
            {
                var component = Regex.Match(fact.Value, @"添付ログに(?<component>[^の。]{2,60})の(?:MQ)?接続");
                if (component.Success &&
                    !factSection.Contains(component.Groups["component"].Value, StringComparison.OrdinalIgnoreCase) ||
                    fact.Value.Contains("MQ", StringComparison.OrdinalIgnoreCase) &&
                    !factSection.Contains("MQ", StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(factSection, @"失敗|failure|failed", RegexOptions.IgnoreCase) ||
                    !factSection.Contains("添付ログ", StringComparison.Ordinal))
                    return "重要Factの欠落: 添付ログに記録された失敗が回答本文にありません。";
                if (Regex.IsMatch(factSection, @"(?:MQ|接続).{0,20}(?:失敗していない|失敗なし|成功した|正常に接続)",
                    RegexOptions.IgnoreCase))
                    return "重要Factの極性反転: 添付ログの接続失敗を成功として扱っています。";
            }
        }
        return null;
    }

    internal static bool IsPriorCaseOutcomePreserved(ResolvedFact fact, string reply)
    {
        var factSection = reply.Split("現時点で断定できない事項", 2,
            StringSplitOptions.None)[0];
        var duration = Regex.Match(fact.Value, @"(?:約)?\d+(?:週間|か月|日)");
        var nonRecurrence = Regex.IsMatch(fact.Value,
            @"再発しなかった|did not occur|did not recur", RegexOptions.IgnoreCase);
        var resolved = Regex.IsMatch(fact.Value,
            @"解消した|復旧した|closed as resolved|解決としてクローズ", RegexOptions.IgnoreCase);
        return factSection.Contains("案件履歴", StringComparison.Ordinal) &&
            (!duration.Success || factSection.Contains(duration.Value, StringComparison.Ordinal)) &&
            (!nonRecurrence || Regex.IsMatch(factSection,
                @"再発しなかった|再発せず|非再発|did not recur|did not occur", RegexOptions.IgnoreCase)) &&
            (!resolved || Regex.IsMatch(factSection,
                @"解消|復旧|解決としてクローズ|resolved", RegexOptions.IgnoreCase));
    }

    private static bool IsImportant(ResolvedFact fact) =>
        fact.Status == FactStatuses.Candidate &&
        (fact.SourceType == "CurrentInquiry" && fact.Key is "ImportantInquiryFailure" or "ImportantInquiryOperation" or "ImportantInquiryVersion" ||
         fact.SourceType == "CurrentCase" &&
         (fact.Key is "PriorCaseOutcome" or "ObservedLogFailure" ||
          fact.Key == "CaseObservation" && (IsServiceRecovery(fact.Value) || IsVersionedFix(fact.Value))));

    private static bool IsServiceRecovery(string value) =>
        ServiceRecovery.IsMatch(value) &&
        value.Contains("After starting", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(value, @"scan completed successfully", RegexOptions.IgnoreCase);

    private static bool IsVersionedFix(string value) =>
        Version.IsMatch(value) && Regex.IsMatch(value, @"不具合.{0,30}修正|修正.{0,30}不具合");

    private static bool ContainsAnchors(string reply, string value)
    {
        var tokens = Regex.Matches(value, @"[ァ-ヴー]{4,}|[一-龥]{2,}|[A-Za-z][A-Za-z0-9_-]+")
            .Select(static match => match.Value)
            .Where(static token => token is not ("社内" or "現在" or "お客様" or "への" or "の"))
            .ToArray();
        return tokens.Length > 0 && tokens.All(token =>
            reply.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static ResolvedFact InquiryFact(string key, string value, string operation,
        ISafetyRedactionService redactor)
    {
        var safe = redactor.RedactForCloud(value);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safe)))
            .ToLowerInvariant()[..16];
        return new ResolvedFact
        {
            FactId = $"inquiry:{hash}", Key = key,
            Statement = "顧客申告の観測または計画。製品仕様の確定ではない。",
            Value = safe, Status = FactStatuses.Candidate,
            Confidence = FactConfidences.Medium, SourceType = "CurrentInquiry",
            EvidenceId = $"inquiry:{hash}", Operation = operation,
            AuthorityLevel = "CustomerReport",
        };
    }
}
