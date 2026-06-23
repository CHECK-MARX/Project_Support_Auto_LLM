using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Core.Answers;

internal static partial class AnswerPostProcessor
{
    public static AnswerDraftResult Process(
        AnswerDraftRequest request,
        AnswerDraftResult result,
        IReadOnlyList<EvidenceItem> evidence,
        double confidence,
        IReadOnlyList<string> warnings)
    {
        var mergedWarnings = new List<string>(warnings);
        var customerReply = result.CustomerReplyDraft;
        var internalMemo = result.InternalMemo;
        var needConfirmations = result.NeedConfirmations.ToList();
        var finalConfidence = confidence;
        var finalEvidence = evidence.ToList();

        if (TryBuildFactBasedLatestVersionReply(request, out var factBasedReply, out var factBasedMemo))
        {
            customerReply = factBasedReply;
            internalMemo = factBasedMemo;
            mergedWarnings.Add("ResolvedFactsに基づいて最新バージョン回答案を補正しました。");
            finalConfidence = Math.Max(finalConfidence, 0.9);
        }
        else if (request.InquiryFocus?.IsFreshnessSensitive == true && !request.Sources.Any(IsOfficialDoc))
        {
            customerReply = BuildFreshnessSafeCustomerReply(request);
            internalMemo = BuildInternalMemo(request, evidence, "鮮度重要質問ですが、OfficialDoc根拠がないため断定回答を抑止しました。");
            needConfirmations.Add(new NeedConfirmationItem
            {
                Question = "メーカー公式情報で最新バージョン、EP/HF、リリース情報、サポート期限を確認してください。",
                Reason = "過去案件だけでは現在の最新情報として断定できません。",
                Priority = "High",
            });
            mergedWarnings.Add("鮮度重要質問のため、OfficialDocなしでは過去案件から最新情報を断定しません。");
            finalConfidence = Math.Min(finalConfidence, 0.35);
        }
        else
        {
            if (ShouldEnforceEmailFormat(request) && !LooksLikeEmailBody(customerReply))
            {
                customerReply = BuildEmailBody(customerReply, request);
                mergedWarnings.Add("お客様向け回答案がメール本文形式ではないため補正しました。");
            }

            if (ShouldEnforceEmailFormat(request) && IsWeakInternalMemo(internalMemo))
            {
                internalMemo = BuildInternalMemo(request, evidence, "LLMの社内メモが不足していたため補完しました。");
                mergedWarnings.Add("社内メモが不足していたため補完しました。");
            }

            if (ShouldBuildEvidenceBackedFallback(request, customerReply, finalEvidence))
            {
                if (finalEvidence.Count == 0)
                {
                    finalEvidence = BuildEvidenceFromSources(request.Sources, request.Settings.MaxEvidenceItems).ToList();
                }

                if (finalEvidence.Count > 0)
                {
                    customerReply = BuildEvidenceBackedCustomerReply(request, finalEvidence);
                    internalMemo = BuildInternalMemo(
                        request,
                        finalEvidence,
                        "LLM回答が送信済み根拠を十分に活用できていなかったため、根拠タイトル/抜粋から保守的に回答案を補完しました。");
                    mergedWarnings.Add("LLM回答が根拠を活用できていなかったため、送信済み根拠から回答案を補完しました。");
                    finalConfidence = Math.Max(finalConfidence, CalculateEvidenceBackedFallbackConfidence(finalEvidence));
                }
            }
        }

        if (request.Sources.Count == 0 || finalEvidence.Count == 0 || finalConfidence < 0.45)
        {
            mergedWarnings.Add("根拠または信頼度が不足しています。回答前に人間が内容を確認してください。");
        }

        customerReply = SanitizeCustomerReplyForExternalUse(customerReply, out var customerReplySanitized);
        if (customerReplySanitized)
        {
            mergedWarnings.Add("お客様向け回答案から過去案件由来の顧客情報・サポート番号・メール断片を除去しました。");
            if (string.IsNullOrWhiteSpace(customerReply))
            {
                customerReply = BuildUnsafeCustomerReplyFallback(request);
                finalConfidence = Math.Min(finalConfidence, 0.35);
            }
        }

        return result with
        {
            CustomerReplyDraft = customerReply.Trim(),
            InternalMemo = internalMemo.Trim(),
            NeedConfirmations = needConfirmations
                .Where(static item => !string.IsNullOrWhiteSpace(item.Question) || !string.IsNullOrWhiteSpace(item.Reason))
                .DistinctBy(static item => $"{item.Priority}|{item.Question}|{item.Reason}")
                .ToList(),
            Evidence = finalEvidence,
            Confidence = Math.Clamp(finalConfidence, 0, 1),
            Warnings = mergedWarnings.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    private static bool TryBuildFactBasedLatestVersionReply(
        AnswerDraftRequest request,
        out string customerReply,
        out string internalMemo)
    {
        customerReply = string.Empty;
        internalMemo = string.Empty;
        var facts = request.FactResolution;
        if (facts is null ||
            !string.Equals(facts.AnswerReadiness, AnswerReadiness.AutoAnswerable, StringComparison.OrdinalIgnoreCase) ||
            !facts.Classification.QuestionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var latestSast = FindConfirmedHighFact(facts, FactKeys.LatestSastVersion);
        var latestEnginePack = FindConfirmedHighFact(facts, FactKeys.LatestEnginePackVersion);
        var latestHotfix = FindConfirmedHighFact(facts, FactKeys.LatestHotfixVersion);
        if (latestSast is null)
        {
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいたCxSAST、Engine Pack（EP）、Hotfix（HF）の最新バージョンについて、確認時点の公式情報では以下の内容です。");
        builder.AppendLine();
        builder.AppendLine($"・CxSAST：{latestSast.Value}");
        if (latestEnginePack is not null)
        {
            builder.AppendLine($"・Engine Pack（EP）：{latestEnginePack.Value}");
        }

        if (latestHotfix is not null)
        {
            builder.AppendLine($"・Hotfix（HF）：{latestHotfix.Value}");
        }

        builder.AppendLine();
        builder.AppendLine("なお、メーカー側で情報が更新される可能性があるため、正式な作業前には念のため公式リリースノートをご確認ください。");
        customerReply = builder.ToString();

        internalMemo = BuildFactInternalMemo(facts);
        return true;
    }

    private static ResolvedFact? FindConfirmedHighFact(FactResolutionResult facts, string key)
    {
        return facts.ResolvedFacts.FirstOrDefault(fact =>
            string.Equals(fact.Key, key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fact.Status, FactStatuses.Confirmed, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fact.Confidence, FactConfidences.High, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildFactInternalMemo(FactResolutionResult facts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("アプリ側ResolvedFactsにより自動回答可能と判定しました。");
        builder.AppendLine($"QuestionType: {string.Join(", ", facts.Classification.QuestionTypes)}");
        builder.AppendLine($"AnswerReadiness: {facts.AnswerReadiness}");
        foreach (var fact in facts.ResolvedFacts)
        {
            builder.AppendLine($"- {fact.Key} = {fact.Value} / {fact.Status} / {fact.Confidence} / {fact.SourceType}");
            if (fact.SourceUrls.Count > 0)
            {
                builder.AppendLine($"  SourceUrls: {string.Join(", ", fact.SourceUrls)}");
            }
        }

        if (facts.CrawlerConflicts.Count > 0)
        {
            builder.AppendLine("Crawler conflicts are diagnostics only:");
            foreach (var conflict in facts.CrawlerConflicts.Take(12))
            {
                builder.AppendLine($"- {conflict}");
            }
        }

        return builder.ToString();
    }

    private static bool ShouldEnforceEmailFormat(AnswerDraftRequest request)
    {
        return request.InquiryFocus is not null;
    }

    private static bool ShouldBuildEvidenceBackedFallback(
        AnswerDraftRequest request,
        string customerReply,
        IReadOnlyList<EvidenceItem> evidence)
    {
        if (request.Sources.Count == 0)
        {
            return false;
        }

        if (request.InquiryFocus?.IsFreshnessSensitive == true)
        {
            return false;
        }

        if (!IsWeakCustomerReply(customerReply))
        {
            return false;
        }

        return evidence.Count > 0 || request.Sources.Any(static source => !string.IsNullOrWhiteSpace(source.Title) || !string.IsNullOrWhiteSpace(source.Text));
    }

    private static bool IsWeakCustomerReply(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = NormalizeWhitespace(value);
        if (string.Equals(normalized, "string", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Contains("断定できる回答内容", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Contains("選択根拠からは", StringComparison.Ordinal) &&
            normalized.Contains("確認できません", StringComparison.Ordinal) &&
            !normalized.Contains("確認できます", StringComparison.Ordinal);
    }

    private static IReadOnlyList<EvidenceItem> BuildEvidenceFromSources(
        IReadOnlyList<SearchSource> sources,
        int maxEvidenceItems)
    {
        var maxItems = maxEvidenceItems > 0 ? maxEvidenceItems : 2;
        return sources
            .OrderByDescending(static source => source.Score ?? 0)
            .ThenBy(static source => source.SourceId, StringComparer.Ordinal)
            .Take(maxItems)
            .Select(static source => new EvidenceItem
            {
                SourceId = source.SourceId,
                SourceType = source.SourceType,
                Title = source.Title,
                Excerpt = BuildExcerpt(source.Text, 240),
                FilePath = source.FilePath,
                SupportNumber = source.SupportNumber,
                Relevance = Math.Clamp(source.Score ?? 0, 0, 1),
            })
            .ToList();
    }

    private static string BuildEvidenceBackedCustomerReply(
        AnswerDraftRequest request,
        IReadOnlyList<EvidenceItem> evidence)
    {
        var customerVisibleEvidence = evidence
            .Where(static item => IsCustomerVisibleSourceType(item.SourceType))
            .ToList();
        if (customerVisibleEvidence.Count == 0)
        {
            return BuildPastCaseOnlySafeCustomerReply(request);
        }

        var builder = new StringBuilder();
        var subject = LooksLikeSupportedOsQuestion(request.InquiryText)
            ? "対応OS"
            : "お問い合わせ内容";
        builder.AppendLine($"お問い合わせいただいた{subject}について、現在AIに送信された根拠から確認できる範囲では、以下の内容が確認できます。");
        builder.AppendLine();

        foreach (var item in customerVisibleEvidence
            .OrderByDescending(static item => item.Relevance)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .Take(5))
        {
            var line = BuildCustomerEvidenceLine(item);
            if (!string.IsNullOrWhiteSpace(line))
            {
                builder.AppendLine($"・{line}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("上記以外の詳細な対応範囲、対象バージョン、制限事項については、メーカー公式情報または製品マニュアルで追加確認が必要です。");
        return builder.ToString();
    }

    private static bool IsCustomerVisibleSourceType(string sourceType)
    {
        return string.Equals(sourceType, "Manual", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "CuratedFactCatalog", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "Curated", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPastCaseOnlySafeCustomerReply(AnswerDraftRequest request)
    {
        var subject = LooksLikeSupportedOsQuestion(request.InquiryText)
            ? "対応OS"
            : "お問い合わせ内容";

        var builder = new StringBuilder();
        builder.AppendLine($"お問い合わせいただいた{subject}について、現在AIに送信された根拠は過去案件情報が中心です。");
        builder.AppendLine();
        builder.AppendLine("過去案件情報には他案件固有の内容が含まれるため、そのままお客様向け回答として転記できません。");
        builder.AppendLine("製品マニュアルまたはメーカー公式情報で、対象バージョン、対応範囲、制限事項を確認したうえで回答内容を整理します。");
        return builder.ToString();
    }

    private static string BuildCustomerEvidenceLine(EvidenceItem item)
    {
        var title = CleanCustomerVisibleText(item.Title);
        var excerpt = CleanCustomerVisibleText(item.Excerpt);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(excerpt))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(excerpt) ||
            (!string.IsNullOrWhiteSpace(title) && excerpt.Contains(title, StringComparison.Ordinal)))
        {
            return TruncateText(title, 160);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return TruncateText(excerpt, 180);
        }

        return $"{TruncateText(title, 120)}（{TruncateText(excerpt, 160)}）";
    }

    private static bool LooksLikeSupportedOsQuestion(string inquiryText)
    {
        return inquiryText.Contains("対応OS", StringComparison.OrdinalIgnoreCase) ||
            inquiryText.Contains("サポートOS", StringComparison.OrdinalIgnoreCase) ||
            inquiryText.Contains("対応オーエス", StringComparison.OrdinalIgnoreCase) ||
            inquiryText.Contains("OS", StringComparison.OrdinalIgnoreCase);
    }

    private static double CalculateEvidenceBackedFallbackConfidence(IReadOnlyList<EvidenceItem> evidence)
    {
        if (evidence.Count == 0)
        {
            return 0.0;
        }

        var average = evidence.Take(5).Average(static item => Math.Clamp(item.Relevance, 0, 1));
        return Math.Round(Math.Clamp(0.45 + average * 0.35, 0.45, 0.8), 2);
    }

    private static bool IsOfficialDoc(SearchSource source)
    {
        return string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEmailBody(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length >= 3)
        {
            return true;
        }

        return value.Contains("お問い合わせ", StringComparison.Ordinal)
            && (value.Contains("確認", StringComparison.Ordinal) || value.Contains("回答", StringComparison.Ordinal));
    }

    private static string BuildEmailBody(string original, AnswerDraftRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいた件について、確認結果を以下に記載します。");
        builder.AppendLine();
        if (string.IsNullOrWhiteSpace(original))
        {
            builder.AppendLine("現時点の参照根拠からは、断定できる回答内容を確認できませんでした。");
        }
        else
        {
            builder.AppendLine(original.Trim());
        }

        builder.AppendLine();
        builder.AppendLine("不足している情報がある場合は、追加確認のうえで回答内容を更新します。");
        return builder.ToString();
    }

    private static string BuildFreshnessSafeCustomerReply(AnswerDraftRequest request)
    {
        var focus = string.IsNullOrWhiteSpace(request.InquiryFocus?.FreshnessReason)
            ? "最新情報"
            : request.InquiryFocus!.FreshnessReason;

        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいた最新情報に関する件について、確認方針を以下に記載します。");
        builder.AppendLine();
        builder.AppendLine($"今回の内容は {focus} に該当するため、メーカー公式情報での確認が必要です。");
        builder.AppendLine("現在選択されている根拠には公式ドキュメントが含まれていないため、過去案件情報だけをもとに最新バージョン、EP/HF、リリース情報、サポート期限を断定することはできません。");
        builder.AppendLine();
        builder.AppendLine("公式情報を確認したうえで、対象バージョン、必要なEP/HF、適用条件、サポート状況を整理して改めて回答します。");
        return builder.ToString();
    }

    private static string SanitizeCustomerReplyForExternalUse(string customerReply, out bool sanitized)
    {
        sanitized = false;
        if (string.IsNullOrWhiteSpace(customerReply))
        {
            return string.Empty;
        }

        var keptLines = new List<string>();
        foreach (var rawLine in customerReply.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                keptLines.Add(string.Empty);
                continue;
            }

            if (LooksLikePastCaseLeakLine(line))
            {
                sanitized = true;
                continue;
            }

            var cleaned = line;
            cleaned = EmailRegex().Replace(cleaned, "[メールアドレス削除]");
            cleaned = PhoneRegex().Replace(cleaned, "[電話番号削除]");
            cleaned = SupportNumberRegex().Replace(cleaned, "[サポート番号削除]");
            cleaned = CompanyNameRegex().Replace(cleaned, "[会社名削除]");
            cleaned = InternalContactRegex().Replace(cleaned, "[担当者情報削除]");
            if (!string.Equals(cleaned, line, StringComparison.Ordinal))
            {
                sanitized = true;
            }

            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                keptLines.Add(cleaned);
            }
        }

        var sanitizedText = string.Join(Environment.NewLine, TrimExcessBlankLines(keptLines)).Trim();
        return sanitized && string.IsNullOrWhiteSpace(sanitizedText)
            ? string.Empty
            : sanitizedText;
    }

    private static bool LooksLikePastCaseLeakLine(string line)
    {
        return SupportNumberRegex().IsMatch(line) ||
            line.Contains("追記部", StringComparison.Ordinal) ||
            line.Contains("お客様への返信案", StringComparison.Ordinal) ||
            line.Contains("お客様ご相談内容", StringComparison.Ordinal) ||
            line.Contains("お客様ご相談", StringComparison.Ordinal) ||
            line.Contains("いつもお世話になっております", StringComparison.Ordinal) ||
            line.Contains("東陽テクニカ", StringComparison.Ordinal) ||
            line.Contains("技術サポート担当", StringComparison.Ordinal) ||
            line.Contains("To:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("From:", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> TrimExcessBlankLines(IEnumerable<string> lines)
    {
        var previousBlank = false;
        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank && previousBlank)
            {
                continue;
            }

            yield return line;
            previousBlank = isBlank;
        }
    }

    private static string BuildUnsafeCustomerReplyFallback(AnswerDraftRequest request)
    {
        var subject = LooksLikeSupportedOsQuestion(request.InquiryText)
            ? "対応OS"
            : "お問い合わせ内容";

        var builder = new StringBuilder();
        builder.AppendLine($"お問い合わせいただいた{subject}について、回答案に過去案件由来の顧客情報が含まれていたため、内容をそのまま利用できません。");
        builder.AppendLine();
        builder.AppendLine("製品マニュアルまたはメーカー公式情報で確認した内容に基づき、顧客情報を含まない形で回答を作成します。");
        return builder.ToString();
    }

    private static string BuildExcerpt(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = NormalizeWhitespace(text);
        return TruncateText(normalized, maxLength);
    }

    private static string CleanCustomerVisibleText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = WindowsPathRegex().Replace(value, "[内部パス削除]");
        cleaned = SourceIdLineRegex().Replace(cleaned, string.Empty);
        return NormalizeWhitespace(cleaned);
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(
            " ",
            value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string TruncateText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static bool IsWeakInternalMemo(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "string", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (SourceIdOnlyRegex().IsMatch(trimmed))
        {
            return true;
        }

        return trimmed.Length < 12;
    }

    private static string BuildInternalMemo(
        AnswerDraftRequest request,
        IReadOnlyList<EvidenceItem> evidence,
        string reason)
    {
        var builder = new StringBuilder();
        builder.AppendLine(reason);
        builder.AppendLine($"問い合わせ焦点: {request.InquiryFocus?.FocusText ?? request.InquiryText}");
        builder.AppendLine($"鮮度重要質問: {(request.InquiryFocus?.IsFreshnessSensitive == true ? "はい" : "いいえ")}");

        if (!string.IsNullOrWhiteSpace(request.InquiryFocus?.FreshnessReason))
        {
            builder.AppendLine($"鮮度理由: {request.InquiryFocus.FreshnessReason}");
        }

        builder.AppendLine($"LLMへ送信した根拠: {request.Sources.Count}件");
        foreach (var source in request.Sources.Take(8))
        {
            builder.AppendLine($"- {source.SourceId} / {source.SourceType} / score={source.Score:0.000} / {source.Title}");
        }

        if (evidence.Count > 0)
        {
            builder.AppendLine("LLM応答で採用された根拠:");
            foreach (var item in evidence.Take(8))
            {
                builder.AppendLine($"- {item.SourceId} / {item.SourceType} / relevance={item.Relevance:0.00}");
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^(?:sourceId\s*[:：]?\s*)?[A-Za-z0-9:_\-.]{4,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceIdOnlyRegex();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s　]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"\b(?:sourceId\s*[:：]?\s*)?[A-Za-z0-9:_\-.]{20,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceIdLineRegex();

    [GeneratedRegex(@"(?<!\d)0{3,}\d{3,}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SupportNumberRegex();

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d{1,3}[-\s]?)?(?:0\d{1,4}[-\s]?\d{1,4}[-\s]?\d{3,4})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?:株式会社|有限会社|合同会社)\s*[^\s、。（）()]{2,}|[^\s、。（）()]{2,}\s*(?:株式会社|有限会社|合同会社)", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyNameRegex();

    [GeneratedRegex(@"[^\s、。（）()]{1,12}\s*(?:様|さん)\b|[^\s、。（）()]{1,12}@[^\s、。（）()]{1,30}", RegexOptions.CultureInvariant)]
    private static partial Regex InternalContactRegex();
}
