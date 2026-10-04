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
        foreach (var header in SupportCaseManager.Ai.Core.Answers.PolishedAnswerValidator.ExtractInquiryTechnicalValues(inquiry))
        {
            var absence = Regex.Match(inquiry, Regex.Escape(header) +
                @"\s*(?:を|が|は)?\s*(?:付与|設定|使用)(?:していない|されていない|しない)");
            if (absence.Success)
                additions.Add(InquiryFact("ImportantInquiryAbsence", absence.Value, "未付与・不使用", redactor));
        }
        foreach (var version in focus?.TargetVersions ?? [])
        {
            if (!inquiry.Contains(version, StringComparison.Ordinal)) continue;
            var qualified = QualifyInquiryVersion(inquiry, version);
            additions.Add(InquiryFact("ImportantInquiryVersion", qualified, "対象Version", redactor));
        }
        if (Regex.IsMatch(inquiry, @"選択肢|(?:以下|次).{0,40}(?:うち|構成|サービス|どれ)|利用可能な|使用可能な"))
            foreach (Match option in Regex.Matches(inquiry, @"(?m)^\s*[0-9０-９]+[．.、)）]\s*(?<value>[^\r\n]{3,120})"))
                additions.Add(InquiryFact("ImportantInquiryOption", option.Groups["value"].Value.Trim(),
                    "問い合わせの選択肢", redactor));
        foreach (Match transition in Regex.Matches(inquiry,
            @"v?(?<from>\d+(?:\.\d+){1,3})から\s*v?(?<to>\d+(?:\.\d+){1,3})へ[^。\r\n]{0,50}(?:更新|移行|バージョンアップ)",
            RegexOptions.IgnoreCase))
            additions.Add(InquiryFact("ImportantInquiryTransition", transition.Value, "版の移行計画", redactor));
        foreach (var clause in Regex.Split(inquiry, @"[。！？\r\n]+").Select(static value => value.Trim()))
        {
            if (Regex.IsMatch(clause, @"誤検知|false positive|enabled|既定.{0,20}変更|デフォルト.{0,20}変更|変更がある|変更の有無|記載.{0,15}理由",
                RegexOptions.IgnoreCase))
                additions.Add(InquiryFact("ImportantInquiryIntent", clause, "問い合わせ論点（結論ではない）", redactor));
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
            "ImportantInquiryAbsence" => "顧客申告の未付与・不使用",
            "ImportantInquiryOption" or "ImportantInquiryIntent" => "顧客の質問・可否未確認",
            "ImportantInquiryTransition" => "顧客の更新計画・未実施",
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
            "ImportantInquiryAbsence" => $"お客様の申告は『{fact.Value}』。製品仕様の証明ではないが、未提示情報として再質問しない",
            "ImportantInquiryOption" => $"問い合わせの選択肢（可否未確認）={fact.Value}",
            "ImportantInquiryIntent" when Regex.IsMatch(fact.Value, @"誤検知|false positive", RegexOptions.IgnoreCase) =>
                "顧客の質問軸=誤検知の可能性についての相談。誤検知という結論は未確認",
            "ImportantInquiryIntent" when fact.Value.Contains("enabled", StringComparison.OrdinalIgnoreCase) =>
                "顧客の質問軸=既定enabledフィールドの変更有無。変更有無の結論は未確認",
            "ImportantInquiryIntent" => $"顧客の質問軸は『{fact.Value}』。質問として本文に保持する",
            "ImportantInquiryTransition" => $"顧客の版の移行計画は『{fact.Value}』。移行元・移行先と操作を一体で保持し、比較資料の版へ置き換えない",
            "CaseObservation" when IsServiceRecovery(fact.Value) =>
                DescribeRecovery(fact.Value),
            "CaseObservation" when IsVersionedFix(fact.Value) =>
                $"{Version.Match(fact.Value).Value}で不具合修正とのメーカー回答があったという案件記録。メーカー原文は未確認",
            _ => fact.Value,
        };
        var dimensions = Describe(fact).Split("; value=", 2, StringSplitOptions.None)[0]
            .Split("; ", StringSplitOptions.None)
            .Where(static field => !field.EndsWith("=原文に明記なし", StringComparison.Ordinal));
        return $"{string.Join("; ", dimensions)}; " +
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
            if (fact.Key == "ImportantInquiryOption")
            {
                if (!NormalizeAxis(reply).Contains(NormalizeAxis(fact.Value), StringComparison.Ordinal))
                    return "重要Factの欠落: 問い合わせの選択肢が回答本文にありません。";
            }
            else if (fact.Key == "ImportantInquiryIntent")
            {
                if (Regex.IsMatch(fact.Value, @"誤検知|false positive", RegexOptions.IgnoreCase) &&
                    !Regex.IsMatch(reply, @"誤検知|false positive", RegexOptions.IgnoreCase) ||
                    fact.Value.Contains("enabled", StringComparison.OrdinalIgnoreCase) &&
                    (!reply.Contains("enabled", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(reply, @"変更|change", RegexOptions.IgnoreCase)) ||
                    Regex.IsMatch(fact.Value, @"変更がある|変更の有無") &&
                    !Regex.IsMatch(reply, @"変更|change", RegexOptions.IgnoreCase))
                    return "重要Factの欠落: 顧客の質問軸・Intentが回答本文にありません。";
            }
            else if (fact.Key == "ImportantInquiryTransition")
            {
                var versions = Version.Matches(fact.Value).Select(static match => match.Value).Take(2).ToArray();
                if (versions.Length == 2 && !Regex.IsMatch(reply,
                    Regex.Escape(versions[0]) + @".{0,12}(?:から|→|to).{0,12}" + Regex.Escape(versions[1]) + @".{0,30}(?:移行|更新|バージョンアップ)",
                    RegexOptions.IgnoreCase))
                    return "重要Factの欠落: 移行元・移行先VersionとOperationの関係がありません。";
            }
            else if (fact.Key == "ImportantInquiryAbsence")
            {
                var header = SupportCaseManager.Ai.Core.Answers.PolishedAnswerValidator.ExtractInquiryTechnicalValues(fact.Value).Single();
                var mentions = Regex.Split(factSection, @"[。！？!?\r\n]").Where(sentence => sentence.Contains(header, StringComparison.Ordinal)).ToArray();
                if (mentions.Length == 0 || mentions.Any(sentence => !Regex.IsMatch(sentence,
                    @"(?:付与|設定|使用)(?:していない|されていない|しない|されておらず)")))
                    return "重要Factの欠落・反転: 顧客申告の未付与・不使用が回答本文に保持されていません。";
                var actions = reply.Split("現時点で断定できない事項", 2, StringSplitOptions.None).Last();
                if (Regex.Split(actions, @"[。！？!?\r\n]").Any(sentence => ReasksKnownAbsence(sentence, header)))
                    return "既知事項の再質問: 顧客が申告済みの未付与・不使用を追加確認として再質問しています。";
            }
            else if (fact.Key == "ImportantInquiryVersion")
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
                var statements = Regex.Split(reply, @"[。！？!?\r\n]")
                    .Where(sentence => sentence.Contains(version, StringComparison.Ordinal) && Regex.IsMatch(sentence, @"修正|fixed", RegexOptions.IgnoreCase));
                if (statements.Any(sentence => Regex.IsMatch(sentence, @"修正(?:されていない|されず|なし|未実施)")))
                    return "重要Factの極性反転: 案件履歴の版付き修正記録を否定しています。";
                if (statements.Any(sentence => Regex.IsMatch(sentence, @"正式保証|公式仕様|公式資料|正式サポート|必ず|確実に")))
                    return "帰属の昇格: CurrentCaseの修正記録を公式仕様や正式保証として扱っています。";
                if (statements.Any(sentence => !Regex.IsMatch(sentence, @"案件履歴|案件記録") ||
                    !Regex.IsMatch(sentence, @"メーカー.{0,12}原文.{0,12}(?:未確認|確認できません|確認されていない)")))
                    return "帰属条件の欠落: 修正記録をメーカー原文未確認の案件履歴として保持していません。";
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

    internal static string PreserveSourceConditions(FactResolutionResult? resolution, string reply,
        ISafetyRedactionService redactor)
    {
        var split = reply.IndexOf("現時点で断定できない事項", StringComparison.Ordinal);
        if (split < 0) return reply;
        var facts = Select(resolution);
        // Normalize a record's attribution only when its complete recovery content
        // already matches a CurrentCase fact. Missing/reversed content still rejects.
        foreach (var fact in facts.Where(fact => fact.SourceType == "CurrentCase" &&
                     fact.Key == "CaseObservation" && IsServiceRecovery(fact.Value) &&
                     !string.IsNullOrWhiteSpace(fact.EvidenceId)))
        {
            var single = resolution! with { ResolvedFacts = [fact] };
            var factBody = reply[..split];
            factBody = Regex.Replace(factBody, @"[^。！？!?\r\n]+", match =>
                !match.Value.Contains("案件履歴", StringComparison.Ordinal) &&
                Regex.IsMatch(match.Value, @"(?:との|という|した).{0,8}記録") &&
                !Regex.IsMatch(match.Value, @"公式|正式|保証|停止していない|完了していない|起動前") &&
                Validate(single, "案件履歴では、" + match.Value) is null
                    ? (match.Value.Contains("確認できる事実:", StringComparison.Ordinal)
                        ? match.Value.Replace("確認できる事実:", "確認できる事実: 案件履歴では、", StringComparison.Ordinal)
                        : "案件履歴では、" + match.Value) : match.Value);
            reply = factBody + reply[split..];
            split = reply.IndexOf("現時点で断定できない事項", StringComparison.Ordinal);
        }
        var conditionalHistory = facts.Where(fact => fact.SourceType == "CurrentCase" &&
            fact.Key == "CaseObservation" && IsVersionedFix(fact.Value)).ToArray();
        if (conditionalHistory.Length > 0)
            reply = reply.Replace("CurrentCase案件履歴", "案件履歴", StringComparison.Ordinal);
        // Attribution applies to every occurrence, including a record placed
        // under uncertainty. Repositioning never removes the original claim.
        foreach (var fact in conditionalHistory)
        {
            var version = Version.Match(fact.Value).Value;
            reply = Regex.Replace(reply, @"[^。！？!?\r\n]+", match =>
                match.Value.Contains(version, StringComparison.Ordinal) &&
                Regex.IsMatch(match.Value, @"案件履歴|案件記録") &&
                Regex.IsMatch(match.Value, @"修正|fixed", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(match.Value, @"メーカー.{0,12}原文.{0,12}(?:未確認|確認できません|確認されていない)")
                    ? match.Value + "（メーカー原文未確認の案件履歴です）" : match.Value);
        }
        split = reply.IndexOf("現時点で断定できない事項", StringComparison.Ordinal);
        var section = reply[..split];
        foreach (var fact in conditionalHistory)
        {
            var version = Version.Match(fact.Value).Value;
            // The existence of the case record is known even when the product
            // conclusion remains unknown. Preserve that record in the facts
            // section without asserting that its manufacturer claim is verified.
            if (!Regex.Split(section, @"[。！？!?\r\n]").Any(sentence =>
                    sentence.Contains(version, StringComparison.Ordinal) &&
                    sentence.Contains("案件履歴", StringComparison.Ordinal) &&
                    Regex.IsMatch(sentence, @"修正|fixed", RegexOptions.IgnoreCase)) &&
                Regex.Split(reply[split..], @"[。！？!?\r\n]").Any(sentence =>
                    sentence.Contains(version, StringComparison.Ordinal) &&
                    Regex.IsMatch(sentence, @"案件履歴|案件記録") &&
                    Regex.IsMatch(sentence, @"修正|fixed", RegexOptions.IgnoreCase) &&
                    !Regex.IsMatch(sentence, @"修正(?:されていない|されず|なし|未実施)|正式保証|公式仕様|正式サポート") &&
                    Regex.IsMatch(sentence, @"メーカー.{0,12}原文.{0,12}(?:未確認|確認できません|確認されていない)")) &&
                fact.Value.Length <= 320 && redactor.RedactForCloud(fact.Value) == fact.Value)
                section += $"案件履歴には「{fact.Value.TrimEnd('。', '\r', '\n')}」との記録があります（メーカー原文未確認の案件履歴です）。";
        }
        foreach (var fact in facts.Where(static fact => fact.Key == "ImportantInquiryAbsence"))
        {
            var header = SupportCaseManager.Ai.Core.Answers.PolishedAnswerValidator.ExtractInquiryTechnicalValues(fact.Value).Single();
            if (!section.Contains(header, StringComparison.Ordinal) && fact.Value.Length <= 160 &&
                redactor.RedactForCloud(fact.Value) == fact.Value)
                section += $"お客様からは「{fact.Value}」との申告があります。";
        }
        reply = section + reply[split..];
        if (conditionalHistory.Length > 0)
        {
            var end = reply.IndexOf("追加で必要な確認", section.Length, StringComparison.Ordinal);
            if (end >= 0)
            {
                var unknown = reply[section.Length..end];
                foreach (var fact in facts.Where(static fact => fact.Key == "ImportantInquiryAbsence"))
                {
                    var header = SupportCaseManager.Ai.Core.Answers.PolishedAnswerValidator.ExtractInquiryTechnicalValues(fact.Value).Single();
                    unknown = Regex.Replace(unknown, @"[^。！？!?\r\n]+", match => ReasksKnownAbsence(match.Value, header)
                        ? (match.Value.Contains("現時点で断定できない事項", StringComparison.Ordinal) ? "現時点で断定できない事項: " : string.Empty) +
                            "メーカー原文が未確認のため、検出の判定と記録された修正の適用範囲は断定できません" : match.Value);
                }
                reply = section + unknown + reply[end..];
            }
        }
        if (conditionalHistory.Length > 0 &&
            !Regex.IsMatch(reply[section.Length..], @"確認できません|断定できません|判断できません|不明|見解が必要"))
            reply = reply.Replace("現時点で断定できない事項:",
                "現時点で断定できない事項: メーカー原文が未確認のため、対象版・個別条件への修正の適用は断定できません。",
                StringComparison.Ordinal);
        // Replace a redundant question only when the existing history itself supplies
        // the specific missing manufacturer provenance. No product fact is invented.
        var actionStart = reply.IndexOf("追加で必要な確認", StringComparison.Ordinal);
        if (conditionalHistory.Length == 0 || actionStart < 0) return reply;
        var actions = reply[actionStart..];
        foreach (var fact in facts.Where(static fact => fact.Key == "ImportantInquiryAbsence"))
        {
            var header = SupportCaseManager.Ai.Core.Answers.PolishedAnswerValidator.ExtractInquiryTechnicalValues(fact.Value).Single();
            actions = Regex.Replace(actions, @"[^。！？!?\r\n]+", match =>
                ReasksKnownAbsence(match.Value, header)
                    ? (match.Value.Contains("追加で必要な確認", StringComparison.Ordinal) ? "追加で必要な確認: " : string.Empty) +
                        "案件履歴のメーカー回答原文と、今回の対象版・検出条件への修正の適用範囲をメーカーへ確認してください"
                    : match.Value);
        }
        return reply[..actionStart] + actions;
    }

    private static bool ReasksKnownAbsence(string sentence, string header) =>
        sentence.Contains(header, StringComparison.Ordinal) && Regex.IsMatch(sentence,
            @"有無|(?:付与|設定|使用|実装)状況|(?:付与|設定|使用)(?:されていない|されている|していない|している|される|する)か|実際に(?:付与|設定|使用)") &&
        Regex.IsMatch(sentence, @"確認|教えて|ご教示|お知らせ|共有");

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
        (fact.SourceType == "CurrentInquiry" && fact.Key is "ImportantInquiryFailure" or "ImportantInquiryOperation" or "ImportantInquiryVersion" or "ImportantInquiryAbsence" or "ImportantInquiryOption" or "ImportantInquiryIntent" or "ImportantInquiryTransition" ||
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

    private static string NormalizeAxis(string value) => Regex.Replace(
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"\s+", string.Empty);

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
