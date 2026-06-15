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
        }

        if (request.Sources.Count == 0 || evidence.Count == 0 || finalConfidence < 0.45)
        {
            mergedWarnings.Add("根拠または信頼度が不足しています。回答前に人間が内容を確認してください。");
        }

        return result with
        {
            CustomerReplyDraft = customerReply.Trim(),
            InternalMemo = internalMemo.Trim(),
            NeedConfirmations = needConfirmations
                .Where(static item => !string.IsNullOrWhiteSpace(item.Question) || !string.IsNullOrWhiteSpace(item.Reason))
                .DistinctBy(static item => $"{item.Priority}|{item.Question}|{item.Reason}")
                .ToList(),
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
            builder.AppendLine("現時点の選択根拠からは、断定できる回答内容を確認できませんでした。");
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
}
