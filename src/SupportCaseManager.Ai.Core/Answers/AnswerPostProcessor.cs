using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Answers;

public static partial class AnswerPostProcessor
{
    public static AnswerDraftResult BuildFailureFallback(
        AnswerDraftRequest request,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);
        var evidenceLimit = request.Settings.UseCoverageAwareEvidenceSelection
            ? request.Sources.Count
            : request.Settings.MaxEvidenceItems;
        var evidence = BuildEvidenceFromSources(request.Sources, evidenceLimit);
        var confidence = CalculateEvidenceBackedFallbackConfidence(evidence);
        return Process(
            request,
            new AnswerDraftResult
            {
                CustomerReplyDraft = string.Empty,
                InternalMemo = $"LLM処理を完了できなかったため、選択済み根拠から回答案を構成しました。エラー={exception.GetType().Name}",
                GeneratedAt = DateTimeOffset.Now,
            },
            evidence,
            confidence,
            [$"LLM処理を完了できなかったため、選択済み根拠から構造化回答を作成しました。エラー={exception.GetType().Name}"]);
    }

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
        var deterministicAnswerCreated = string.IsNullOrWhiteSpace(result.CustomerReplyDraft);

        if (TryBuildFactBasedLatestVersionReply(request, out var factBasedReply, out var factBasedMemo))
        {
            customerReply = factBasedReply;
            internalMemo = factBasedMemo;
            mergedWarnings.Add("ResolvedFactsに基づいて最新バージョン回答案を補正しました。");
            finalConfidence = Math.Max(finalConfidence, 0.9);
        }

        if (HowToAnswerComposer.IsAnalysisHowTo(request) &&
            !HowToAnswerComposer.HasRequiredStructure(customerReply) &&
            HowToAnswerComposer.TryComposeAnalysis(request, out var structuredHowToReply))
        {
            customerReply = structuredHowToReply;
            mergedWarnings.Add("HowTo回答を選択済み根拠に基づく操作順へ補正しました。");
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
                var evidenceLimit = request.Settings.UseCoverageAwareEvidenceSelection
                    ? request.Sources.Count
                    : request.Settings.MaxEvidenceItems;
                var sourceEvidence = BuildEvidenceFromSources(request.Sources, evidenceLimit);
                finalEvidence = MergeEvidence(finalEvidence, sourceEvidence, evidenceLimit).ToList();

                if (finalEvidence.Count > 0)
                {
                    var usedAnalysisFallback = TryBuildQacAnalysisProcedureReply(request, out var analysisReply);
                    var streamReply = string.Empty;
                    var usedStreamFallback = !usedAnalysisFallback &&
                        TryBuildValidateStreamReply(request, out streamReply);
                    var procedureReply = string.Empty;
                    var usedProcedureFallback = !usedAnalysisFallback && !usedStreamFallback &&
                        TryBuildValidateUploadProcedureReply(request, out procedureReply);
                    var fileDeliveryReply = string.Empty;
                    var usedFileDeliveryFallback = !usedAnalysisFallback && !usedStreamFallback &&
                        !usedProcedureFallback && TryBuildFileDeliveryAccessReply(request, out fileDeliveryReply);
                    customerReply = usedAnalysisFallback
                        ? analysisReply
                        : usedStreamFallback
                            ? streamReply
                            : usedProcedureFallback
                                ? procedureReply
                                : usedFileDeliveryFallback
                                    ? fileDeliveryReply
                                    : BuildEvidenceBackedCustomerReply(request, finalEvidence);
                    internalMemo = BuildInternalMemo(
                        request,
                        finalEvidence,
                        "LLM回答が送信済み根拠を十分に活用できていなかったため、根拠タイトル/抜粋から保守的に回答案を補完しました。");
                    mergedWarnings.Add(usedAnalysisFallback
                        ? "LLM回答が根拠を十分に反映できなかったため、送信済み根拠からQACプロジェクト解析手順を補完しました。"
                        : usedStreamFallback
                            ? "LLM回答が根拠を十分に反映できなかったため、送信済み根拠からValidate Streamの概要と設定方法を補完しました。"
                            : usedProcedureFallback
                                ? "LLM回答が根拠手順を十分に反映できなかったため、送信済み根拠からValidateアップロード手順を補完しました。"
                                : usedFileDeliveryFallback
                                    ? "LLM回答が類似案件を十分に活用できなかったため、送信済み根拠からファイル提供障害の確認事項と代替案を補完しました。"
                                    : "LLM回答が根拠を活用できていなかったため、送信済み根拠から回答案を補完しました。");
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

        if (deterministicAnswerCreated && IsHowToQuestion(request) &&
            !FreshnessIntentPolicy.IsOperationalAccessOrDeliveryInquiry(request.InquiryText) &&
            !request.Sources.Any(static source => ContainsAny(source.Title, "Fiebie", "Fibe") || ContainsAny(source.Text, "Fiebie", "Fibe")) &&
            !ContainsAny(customerReply, "【概要】", "【設定方法】", "【確認事項】") &&
            !HowToAnswerComposer.HasRequiredStructure(customerReply))
        {
            var customerVisibleEvidence = finalEvidence
                .Where(static item => IsCustomerVisibleSourceType(item.SourceType))
                .ToList();
            customerReply = customerVisibleEvidence.Count == 0
                ? BuildPastCaseOnlySafeCustomerReply(request, finalEvidence
                    .Where(static item => IsPastCaseSourceType(item.SourceType))
                    .ToList())
                : DeterministicAnswerComposer.ComposeHowTo(customerVisibleEvidence);
            mergedWarnings.Add("LLMなしでEvidenceのみからHowTo回答の見出し構造を生成しました。");
        }

        if (deterministicAnswerCreated && IsProductSpecificationQuestion(request) &&
            !finalEvidence.Any(static item =>
                string.Equals(item.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.SourceType, "Manual", StringComparison.OrdinalIgnoreCase)))
        {
            customerReply = DeterministicAnswerComposer.ComposeManufacturerConfirmation(finalEvidence);
            mergedWarnings.Add("製品仕様の権威根拠がないため、メーカー確認Draftを生成しました。");
        }

        customerReply = CustomerReplyRecipientFormatter.EnsureHeader(request.Case, customerReply);

        finalEvidence = ClassifyEvidenceRoles(request, finalEvidence).ToList();

        var claims = BuildEvidenceClaims(finalEvidence);
        var readiness = DetermineDraftReadiness(request, finalEvidence, claims, customerReply);
        var referenceAvailable = finalEvidence.Count(static item => HasReferenceMetadata(item));
        var referenceDisplayed = finalEvidence.Count(item =>
            HasReferenceMetadata(item) && ReferenceAppearsInReply(item, customerReply));
        var referenceMissingFromIndex = finalEvidence.Count(static item =>
            !HasReferenceMetadata(item) &&
            (!string.IsNullOrWhiteSpace(item.DocumentTitle) || !string.IsNullOrWhiteSpace(item.Title)));

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
            Readiness = readiness,
            DeterministicAnswerCreated = deterministicAnswerCreated,
            Claims = claims,
            ReferenceAvailable = referenceAvailable,
            ReferenceDisplayed = referenceDisplayed,
            ReferenceMissingFromIndex = referenceMissingFromIndex,
        };
    }

    private static bool HasReferenceMetadata(EvidenceItem item) =>
        item.PageNumber is > 0 ||
        !string.IsNullOrWhiteSpace(item.SectionTitle) ||
        !string.IsNullOrWhiteSpace(item.Url);

    private static bool ReferenceAppearsInReply(EvidenceItem item, string reply)
    {
        var title = item.DocumentTitle ?? item.Title;
        if (!string.IsNullOrWhiteSpace(title) && reply.Contains(title, StringComparison.Ordinal))
        {
            return true;
        }

        return item.PageNumber is > 0 && reply.Contains($"Page {item.PageNumber.Value}", StringComparison.Ordinal);
    }

    private static IReadOnlyList<EvidenceItem> ClassifyEvidenceRoles(
        AnswerDraftRequest request,
        IReadOnlyList<EvidenceItem> evidence)
    {
        var relevant = evidence
            .Where(item => IsEvidenceRelevantToInquiry(request, item))
            .OrderByDescending(static item => item.Relevance)
            .Select(static item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var primaryId = relevant.FirstOrDefault();
        return evidence.Select(item => item with
        {
            EvidenceRole = item.Excerpt.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
                item.Excerpt.Contains("競合", StringComparison.Ordinal)
                ? "Conflicting"
                : !relevant.Contains(item.SourceId)
                    ? "Irrelevant"
                    : string.Equals(item.SourceId, primaryId, StringComparison.Ordinal)
                        ? "Primary"
                        : "Supporting"
        }).ToList();
    }

    private static IReadOnlyList<Claim> BuildEvidenceClaims(IReadOnlyList<EvidenceItem> evidence)
    {
        return evidence
            .Where(static item => !string.IsNullOrWhiteSpace(item.Excerpt))
            .Select(static item => new Claim
            {
                Statement = BuildClaimStatement(item),
                SupportingFactIds = [item.SourceId],
                SupportLevel = ClaimSupportLevels.Supported,
                CustomerVisible = IsCustomerVisibleSourceType(item.SourceType),
            })
            .ToList();
    }

    private static bool IsHowToQuestion(AnswerDraftRequest request) =>
        request.FactResolution?.Classification.QuestionTypes.Contains(QuestionTypes.HowToQuestion, StringComparer.OrdinalIgnoreCase) == true ||
        ContainsAny(request.InquiryText, "手順", "方法", "設定方法", "how to", "procedure");

    private static bool IsProductSpecificationQuestion(AnswerDraftRequest request) =>
        request.FactResolution?.Classification.QuestionTypes.Contains(QuestionTypes.FeatureAvailabilityQuestion, StringComparer.OrdinalIgnoreCase) == true;

    private static string BuildClaimStatement(EvidenceItem item)
    {
        var statement = NormalizeWhitespace(item.Excerpt);
        return statement.Length <= 300 ? statement : statement[..300] + "...";
    }

    private static string DetermineDraftReadiness(
        AnswerDraftRequest request,
        IReadOnlyList<EvidenceItem> evidence,
        IReadOnlyList<Claim> claims,
        string customerReply)
    {
        if (evidence.Count == 0 || claims.Count == 0)
        {
            return AnswerReadiness.InsufficientEvidence;
        }

        var asksForProductSpec = request.FactResolution?.Classification.QuestionTypes
            .Contains(QuestionTypes.FeatureAvailabilityQuestion, StringComparer.OrdinalIgnoreCase) == true;
        var hasAuthoritativeEvidence = evidence.Any(static item =>
            string.Equals(item.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.SourceType, "Manual", StringComparison.OrdinalIgnoreCase));
        if (asksForProductSpec && !hasAuthoritativeEvidence)
        {
            return AnswerReadiness.NeedsManufacturerConfirmation;
        }

        if (request.FactResolution?.Conflicts.Count > 0 || claims.Any(static claim => claim.Conflicting))
        {
            return AnswerReadiness.NeedsReview;
        }

        if (ContainsCustomerContextLeakage(customerReply) || ContainsUnresolvedRequiredContent(request, customerReply))
        {
            return AnswerReadiness.NeedsReview;
        }

        if (RequiresAtomicCliCommand(request) && !HasAtomicCliCommand(request.Sources))
        {
            return AnswerReadiness.NeedsReview;
        }

        if (asksForProductSpec && evidence.All(static item => IsPastCaseSourceType(item.SourceType)))
        {
            return AnswerReadiness.NeedsManufacturerConfirmation;
        }

        return AnswerReadiness.CustomerReady;
    }

    private static bool RequiresAtomicCliCommand(AnswerDraftRequest request)
    {
        return ContainsAny(request.InquiryText, "CLI", "コマンド", "オプション", "qacli") ||
            request.FactResolution?.Classification.QuestionTypes.Contains("CommandQuestion", StringComparer.OrdinalIgnoreCase) == true;
    }

    private static bool HasAtomicCliCommand(IReadOnlyList<SearchSource> sources)
    {
        return sources
            .Where(static source => IsCustomerVisibleSourceType(source.SourceType))
            .SelectMany(static source => ExtractAtomicCommands(source.Text))
            .Any();
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

        if (request.InquiryFocus?.IsFreshnessSensitive == true && !request.Sources.Any(IsOfficialDoc))
        {
            return false;
        }

        if (FreshnessIntentPolicy.IsOperationalAccessOrDeliveryInquiry(request.InquiryText) &&
            request.Sources.Any(static source =>
                IsPastCaseSourceType(source.SourceType) ||
                ContainsAny(source.Title ?? string.Empty, "Fiebie", "Fibe") ||
                ContainsAny(source.Text ?? string.Empty, "Fiebie", "Fibe", "/api/file/download/content")))
        {
            return true;
        }

        if (HasHighConfidencePastCaseActionEvidence(request.Sources))
        {
            return true;
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

        if (normalized.Contains("LLM応答を解析できませんでした", StringComparison.Ordinal) ||
            normalized.Contains("回答内容を確認してください", StringComparison.Ordinal))
        {
            return true;
        }

        return (normalized.Contains("選択根拠からは", StringComparison.Ordinal) ||
                normalized.Contains("参照根拠からは", StringComparison.Ordinal) ||
                normalized.Contains("根拠からは", StringComparison.Ordinal)) &&
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
                DocumentTitle = source.DocumentTitle,
                PageNumber = source.PageNumber,
                SectionTitle = source.SectionTitle,
                Url = source.Url,
                ChunkId = source.ChunkId,
                DocumentId = source.DocumentId,
                ContentHash = source.ContentHash,
                ArchivePath = source.ArchivePath,
                EntryPath = source.EntryPath,
                Excerpt = BuildExcerpt(source.Text, 500),
                FilePath = source.FilePath,
                SupportNumber = source.SupportNumber,
                Relevance = Math.Clamp(source.Score ?? 0, 0, 1),
            })
            .ToList();
    }

    private static IReadOnlyList<EvidenceItem> MergeEvidence(
        IReadOnlyList<EvidenceItem> parsedEvidence,
        IReadOnlyList<EvidenceItem> sourceEvidence,
        int maxEvidenceItems)
    {
        var maxItems = maxEvidenceItems > 0 ? maxEvidenceItems : Math.Max(parsedEvidence.Count + sourceEvidence.Count, 2);
        return parsedEvidence
            .Concat(sourceEvidence)
            .Where(static item => !string.IsNullOrWhiteSpace(item.SourceId))
            .GroupBy(static item => item.SourceId, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static item => item.Relevance)
                .ThenByDescending(MetadataCompleteness)
                .First())
            .OrderByDescending(static item => item.Relevance)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .Take(maxItems)
            .ToList();
    }

    private static int MetadataCompleteness(EvidenceItem item) =>
        (string.IsNullOrWhiteSpace(item.DocumentTitle) ? 0 : 1) +
        (item.PageNumber is > 0 ? 1 : 0) +
        (string.IsNullOrWhiteSpace(item.SectionTitle) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(item.Url) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(item.DocumentId) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(item.ChunkId) ? 0 : 1);

    private static string BuildEvidenceBackedCustomerReply(
        AnswerDraftRequest request,
        IReadOnlyList<EvidenceItem> evidence)
    {
        var customerVisibleEvidence = evidence
            .Where(static item => IsCustomerVisibleSourceType(item.SourceType))
            .Where(item => IsEvidenceRelevantToInquiry(request, item))
            .ToList();
        var pastCaseEvidence = evidence
            .Where(static item => IsPastCaseSourceType(item.SourceType))
            .ToList();
        if (customerVisibleEvidence.Count == 0)
        {
            if (evidence.Any(static item => IsCustomerVisibleSourceType(item.SourceType)))
            {
                return BuildNoDirectEvidenceCustomerReply(request);
            }

            return BuildPastCaseOnlySafeCustomerReply(request, pastCaseEvidence);
        }

        var builder = new StringBuilder();
        var subject = BuildInquirySubject(request);
        builder.AppendLine($"お問い合わせいただいた{subject}について、選択された根拠を確認した結果を以下に記載します。");
        builder.AppendLine();
        builder.AppendLine("要点");
        builder.AppendLine("・現時点で選択されている公式情報、マニュアル、類似過去案件から確認できる範囲で回答案を整理しています。");
        builder.AppendLine();
        builder.AppendLine("確認できた内容");

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

        var supportingPastCaseLines = pastCaseEvidence
            .Where(item => IsEvidenceRelevantToInquiry(request, item))
            .OrderByDescending(static item => item.Relevance)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .Select(BuildPastCaseEvidenceLine)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        if (supportingPastCaseLines.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("参考となる過去案件の技術情報");
            foreach (var line in supportingPastCaseLines)
            {
                builder.AppendLine($"・{line}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("確認が必要な内容");
        if (request.InquiryFocus?.IsFreshnessSensitive == true)
        {
            builder.AppendLine("・最新バージョン、EP/HF、リリース情報などは更新される可能性があるため、回答前にメーカー公式情報で最終確認が必要です。");
        }
        else
        {
            builder.AppendLine("・対象バージョン、適用条件、制限事項は、お客様環境に合わせて最終確認が必要です。");
        }

        builder.AppendLine();
        builder.AppendLine("次の対応");
        builder.AppendLine("・上記の確認内容をもとに、必要な条件を確認したうえで回答内容を確定します。");
        return builder.ToString();
    }

    private static bool TryBuildValidateStreamReply(
        AnswerDraftRequest request,
        out string customerReply)
    {
        customerReply = string.Empty;
        var inquiry = NormalizeWhitespace(request.InquiryText);
        var asksAboutStream = ContainsAny(inquiry, "Stream", "stream", "ストリーム");
        var asksForOverview = ContainsAny(inquiry, "機能", "概要", "どのような", "what is", "purpose");
        var asksForConfiguration = ContainsAny(inquiry, "設定", "手順", "方法", "configure", "configuration", "setup");
        if (!inquiry.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
            !asksAboutStream ||
            !asksForOverview ||
            !asksForConfiguration)
        {
            return false;
        }

        var visibleSources = request.Sources
            .Where(static source => IsCustomerVisibleSourceType(source.SourceType))
            .ToList();
        var sourceText = string.Join(Environment.NewLine, visibleSources.Select(static source => source.Text));
        var compactSource = NormalizeWhitespace(sourceText);
        if (!ContainsAny(compactSource, "Stream", "stream", "ストリーム"))
        {
            return false;
        }

        var supportsTracking =
            ContainsAny(compactSource, "ストリームのビルドをトラッキング", "track stream builds", "tracking builds in a stream") ||
            (ContainsAny(compactSource, "トラッキング", "tracking") && ContainsAny(compactSource, "ストリーム", "stream"));
        var supportsNewIssueFocus = ContainsAny(
            compactSource,
            "新しい問題点に集中",
            "新しい問題に集中",
            "focus on new issues",
            "focus on possible new issues");
        var supportsStreamAssociation =
            ContainsAny(compactSource, "特定のストリームに接合", "特定のストリームに関連", "associate", "join") &&
            ContainsAny(compactSource, "Perforce QACプロジェクト", "Perforce QAC project");
        var supportsStreamCreation =
            ContainsAny(compactSource, "Validate内でストリームを生成", "Validateでストリームを生成", "create a stream in Validate") ||
            supportsStreamAssociation;
        var atomicCommands = visibleSources
            .SelectMany(static source => ExtractAtomicCommands(source.Text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var streamCommand = atomicCommands.FirstOrDefault(static command =>
            command.Contains("validate build", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("--stream", StringComparison.OrdinalIgnoreCase));
        var validateConfigCommand = atomicCommands.FirstOrDefault(static command =>
            command.Contains("validate config", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("--validate-project", StringComparison.OrdinalIgnoreCase));
        var supportsCliStream = !string.IsNullOrWhiteSpace(streamCommand);
        var supportsValidateProjectOption = !string.IsNullOrWhiteSpace(validateConfigCommand);
        var supportsProjectConnection =
            supportsValidateProjectOption ||
            ContainsAny(compactSource, "qacli validate connect", "qaclivalidateconnect", "プロジェクト間の接続", "プロジェクトを結合");

        if ((!supportsTracking && !supportsNewIssueFocus) ||
            (!supportsStreamCreation && !supportsStreamAssociation && !supportsCliStream && !supportsValidateProjectOption))
        {
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいたValidateのストリーム機能と設定方法について、確認した根拠に基づきご案内します。");
        builder.AppendLine();
        builder.AppendLine("【概要】");
        if (supportsTracking && supportsNewIssueFocus)
        {
            builder.AppendLine("Validateのストリームは、プロジェクトのビルドを継続的に追跡し、開発者がローカルコピーで作業している間に発生した可能性のある新しい問題へ集中して確認するための機能です。");
        }
        else if (supportsTracking)
        {
            builder.AppendLine("Validateのストリームは、プロジェクトのビルドを継続的に追跡するための機能です。");
        }
        else
        {
            builder.AppendLine("Validateのストリームは、プロジェクトの異なるバージョンを追跡し、対象のPerforce QACプロジェクトを特定のストリームへ関連付けて管理するための機能です。");
        }

        builder.AppendLine();
        builder.AppendLine("【設定方法】");
        var step = 1;
        if (supportsProjectConnection)
        {
            if (supportsValidateProjectOption)
            {
                builder.AppendLine($"{step++}. 対象のPerforce QACプロジェクトをValidateへ接続します。CLIでは `{validateConfigCommand}` を使用します。");
            }
            else
            {
                builder.AppendLine($"{step++}. 対象のPerforce QACプロジェクトとValidateプロジェクトの接続を作成します。");
            }
        }

        if (supportsStreamCreation || supportsStreamAssociation)
        {
            builder.AppendLine($"{step++}. Validateでストリームを作成し、対象のPerforce QACプロジェクトをそのストリームへ関連付けます。");
        }

        if (supportsCliStream)
        {
            builder.AppendLine($"{step}. コマンドラインからビルドを登録する場合は、`{streamCommand}` を使用します。");
        }

        builder.AppendLine();
        builder.AppendLine("【注意点】");
        builder.AppendLine("・設定前に、対象プロジェクトがValidateへ接続されていることと、ストリームを利用できる権限があることをご確認ください。");
        builder.AppendLine("・画面項目や利用可能なオプションは製品バージョンによって異なる場合があるため、ご利用バージョンのマニュアルで最終確認してください。");
        builder.AppendLine();
        builder.AppendLine("以上、よろしくお願いいたします。");
        customerReply = builder.ToString();
        return true;
    }

    private static bool TryBuildQacAnalysisProcedureReply(
        AnswerDraftRequest request,
        out string customerReply)
    {
        if (HowToAnswerComposer.TryComposeAnalysisCli(request, out customerReply))
        {
            return true;
        }

        return HowToAnswerComposer.TryComposeAnalysis(request, out customerReply);
    }

    private static bool TryBuildValidateUploadProcedureReply(
        AnswerDraftRequest request,
        out string customerReply)
    {
        customerReply = string.Empty;
        var inquiry = NormalizeWhitespace(request.InquiryText);
        if (!inquiry.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
            !inquiry.Contains("アップロード", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var visibleSources = request.Sources
            .Where(static source => IsCustomerVisibleSourceType(source.SourceType))
            .ToList();
        var sourceText = string.Join(Environment.NewLine, visibleSources.Select(static source => source.Text));
        var compactSource = NormalizeWhitespace(sourceText);
        var guiSource = visibleSources.FirstOrDefault(static source =>
            (source.Text.Contains("ポータル", StringComparison.OrdinalIgnoreCase) ||
             source.Text.Contains("Portals", StringComparison.OrdinalIgnoreCase)) &&
            source.Text.Contains("Validate", StringComparison.OrdinalIgnoreCase) &&
            (source.Text.Contains("解析結果をアップロード", StringComparison.OrdinalIgnoreCase) ||
             source.Text.Contains("Upload Results", StringComparison.OrdinalIgnoreCase)));
        var atomicCommands = visibleSources
            .SelectMany(static source => ExtractAtomicCommands(source.Text))
            .Where(static command => command.Contains("validate build", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasGuiProcedure = guiSource is not null;
        var hasCliProcedure = atomicCommands.Count > 0;
        if (!hasGuiProcedure && !hasCliProcedure)
        {
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいた、Perforce QACの解析結果をValidateへアップロードする方法についてご案内します。");
        if (hasGuiProcedure)
        {
            builder.AppendLine();
            builder.AppendLine("【GUIでの手順】");
            builder.AppendLine("1. QA GUIで、解析済みの対象プロジェクトを開きます。");
            builder.AppendLine("2. ［ポータル］>［Validate］>［解析結果をアップロード］を選択します。");
            builder.AppendLine("3. 必要に応じてソースコードエンコーディングとビルド名を指定し、アップロードを実行します。未指定の場合、エンコーディングはシステム設定、ビルド名はValidateサーバ側の割り当てが使用されます。");
        }

        if (hasCliProcedure)
        {
            builder.AppendLine();
            builder.AppendLine("【CLIでの手順】");
            builder.AppendLine("対象プロジェクトのディレクトリを指定して、次のコマンドを実行します。");
            foreach (var command in atomicCommands.Take(2))
            {
                builder.AppendLine($"`{command}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("【事前条件】");
        builder.AppendLine("・Validateで認証済みで、アップロードに必要な権限があること");
        builder.AppendLine("・Validate側に対象プロジェクトが作成され、Perforce QACプロジェクトとの接続が完了していること");
        builder.AppendLine("・必要なビルドライセンスを利用できること");
        builder.AppendLine();
        builder.AppendLine("以上、よろしくお願いいたします。");
        customerReply = builder.ToString();
        return true;
    }

    private static bool TryBuildFileDeliveryAccessReply(
        AnswerDraftRequest request,
        out string customerReply)
    {
        customerReply = string.Empty;
        if (!FreshnessIntentPolicy.IsOperationalAccessOrDeliveryInquiry(request.InquiryText))
        {
            return false;
        }

        var sourceText = string.Join(
            Environment.NewLine,
            request.Sources.Select(static source => string.Join(' ', source.Title, source.Text, source.SectionTitle)));
        var compactSource = NormalizeWhitespace(sourceText);
        var hasFiebie = ContainsAny(compactSource, "Fiebie", "Fibe", "/api/file/download/content");
        var hasPastCase = request.Sources.Any(static source => IsPastCaseSourceType(source.SourceType));
        if (!hasFiebie && !hasPastCase)
        {
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいたQACインストーラの入手について、類似の対応済み案件を含む選択根拠から確認できた内容をご案内します。");
        builder.AppendLine();
        builder.AppendLine("【確認結果】");
        builder.AppendLine(hasFiebie
            ? "・ご記載の「Fibe」は、ファイル転送サービス「Fiebie」を指すものと考えられます。"
            : "・ダウンロードサイトへのアクセスまたはファイル取得の段階で制限されている可能性があります。");
        builder.AppendLine();
        builder.AppendLine("【主な原因候補】");
        builder.AppendLine("・社内Webフィルタまたは通信制限");
        builder.AppendLine("・プロキシ／SSL検査によるダウンロード通信の遮断");
        builder.AppendLine("・実行ファイル（.exe）やファイルサイズに対するダウンロード制限");
        builder.AppendLine("・ブラウザ、端末、または利用中のネットワーク経路の影響");
        builder.AppendLine();
        builder.AppendLine("【確認をお願いしたい事項】");
        builder.AppendLine("・表示されたエラー画面と、ログイン後のどの段階で失敗するか");
        builder.AppendLine("・最新版のEdgeまたはChromeでも同じ事象になるか");
        builder.AppendLine(hasFiebie
            ? "・情報システム部門でFiebieのドメインおよびダウンロード通信が許可されているか"
            : "・情報システム部門で対象サイトのドメインおよびダウンロード通信が許可されているか");
        builder.AppendLine();
        builder.AppendLine("【代替手段】");
        builder.AppendLine("・別のネットワークまたは端末から取得できるかをご確認ください。");
        builder.AppendLine("・難しい場合は、お客様指定の安全なファイル転送サービス、または外部アップロードを許可したSharePoint／OneDriveの利用可否を確認します。");
        builder.AppendLine("・メール添付は、容量や実行ファイル制限で遮断される可能性が高いため推奨しません。");
        if (hasPastCase)
        {
            builder.AppendLine();
            builder.AppendLine("【類似案件】");
            builder.AppendLine("・同様の案内後に取得できた事例がありますが、具体的な解消方法までは記録されていません。");
        }

        builder.AppendLine();
        builder.AppendLine("上記をご確認いただき、エラー画面と失敗する段階をご連絡ください。確認結果に応じて次の対応をご案内します。");
        builder.AppendLine();
        builder.AppendLine("以上、よろしくお願いいたします。");
        customerReply = builder.ToString();
        return true;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractAtomicCommands(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        // A command must be complete inside one SearchSource. The expression accepts
        // only option/value tokens and therefore stops before explanatory prose.
        const string pattern = @"(?<![A-Za-z0-9_])qacli[ \t]+(?:validate[ \t]+[A-Za-z][A-Za-z0-9_-]*|analyze)(?:[ \t]+(?:--?[A-Za-z][A-Za-z0-9_-]*(?:<[^>\r\n]+>)?|<[^>\r\n]+>|[A-Za-z0-9_./:-]+))*";
        var normalized = value;
        foreach (Match match in Regex.Matches(normalized, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var command = NormalizeWhitespace(match.Value).TrimEnd('。', ',', '、', ';', '；');
            var trailing = normalized[(match.Index + match.Length)..];
            if (command.Length > 0 &&
                !Regex.IsMatch(command, @"^qacli\s+analyze\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !Regex.IsMatch(command, @"(?:^|\s)-P(?:\s*$|\s+--?[A-Za-z]|\s+-[A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                (trailing.Length == 0 || char.IsWhiteSpace(trailing[0]) || ".,、。;；)）]］".Contains(trailing[0])))
            {
                yield return command;
            }
        }
    }

    private static bool IsCustomerVisibleSourceType(string sourceType)
    {
        return string.Equals(sourceType, "Manual", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "CuratedFactCatalog", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "Curated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPastCaseSourceType(string sourceType)
    {
        return string.Equals(sourceType, "PastCase", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "PastCaseNote", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "PastAnswer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "ExactPastAnswer", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildNoDirectEvidenceCustomerReply(AnswerDraftRequest request)
    {
        var subject = BuildInquirySubject(request);
        var builder = new StringBuilder();
        builder.AppendLine($"お問い合わせいただいた{subject}について、選択された根拠を確認しました。");
        builder.AppendLine();
        builder.AppendLine("確認できた内容");
        builder.AppendLine("・現在選択されている根拠からは、問い合わせ内容に直接該当する回答根拠を確認できませんでした。");
        builder.AppendLine();
        builder.AppendLine("確認が必要な内容");
        builder.AppendLine("・問い合わせ内容に対応するマニュアル、過去案件、公式情報を選択し直したうえで再確認が必要です。");
        builder.AppendLine();
        builder.AppendLine("次の対応");
        builder.AppendLine("・該当する根拠を確認でき次第、回答内容を整理します。");
        return builder.ToString();
    }

    private static string BuildPastCaseOnlySafeCustomerReply(
        AnswerDraftRequest request,
        IReadOnlyList<EvidenceItem> pastCaseEvidence)
    {
        var subject = BuildInquirySubject(request);
        var actionLines = pastCaseEvidence
            .OrderByDescending(static item => item.Relevance)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .Select(BuildPastCaseActionEvidenceLine)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToList();

        var builder = new StringBuilder();
        if (actionLines.Count > 0)
        {
            builder.AppendLine($"お問い合わせいただいた{subject}について、類似の対応済み案件から確認できる内容をもとに回答案を整理します。");
            builder.AppendLine();
            builder.AppendLine("確認できた対応内容");
            foreach (var line in actionLines)
            {
                builder.AppendLine($"・{line}");
            }

            builder.AppendLine();
            builder.AppendLine("確認が必要な内容");
            builder.AppendLine("・お客様環境の対象バージョン、OS、送付対象ファイル、適用条件が類似案件と同じか確認が必要です。");
            builder.AppendLine();
            builder.AppendLine("次の対応");
            builder.AppendLine("・上記条件に相違がなければ、同様の対応内容をもとにご案内します。");
            return builder.ToString();
        }

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

    private static string BuildPastCaseEvidenceLine(EvidenceItem item)
    {
        var text = CleanPastCaseSummaryText(item.Excerpt);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = CleanPastCaseSummaryText(item.Title);
        }

        var sentence = PickUsefulPastCaseSentence(text);
        return string.IsNullOrWhiteSpace(sentence)
            ? string.Empty
            : TruncateText(sentence, 240);
    }

    private static string BuildPastCaseActionEvidenceLine(EvidenceItem item)
    {
        var text = CleanPastCaseSummaryText(item.Excerpt);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = CleanPastCaseSummaryText(item.Title);
        }

        var sentence = PickUsefulPastCaseSentence(text, requireActionSignal: true);
        return string.IsNullOrWhiteSpace(sentence)
            ? string.Empty
            : TruncateText(sentence, 260);
    }

    private static string CleanPastCaseSummaryText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = WindowsPathRegex().Replace(value, " ");
        cleaned = EmailRegex().Replace(cleaned, " ");
        cleaned = PhoneRegex().Replace(cleaned, " ");
        cleaned = SupportNumberRegex().Replace(cleaned, " ");
        cleaned = CompanyNameRegex().Replace(cleaned, " ");
        cleaned = InternalContactRegex().Replace(cleaned, " ");
        cleaned = PastCaseSeparatorRegex().Replace(cleaned, " ");
        cleaned = PastCaseHeaderRegex().Replace(cleaned, " ");
        cleaned = NormalizeWhitespace(cleaned);
        return cleaned
            .Replace("お客様ご相談内容", string.Empty, StringComparison.Ordinal)
            .Replace("お客様ご相談", string.Empty, StringComparison.Ordinal)
            .Replace("お客様への返信案", string.Empty, StringComparison.Ordinal)
            .Trim(' ', '、', '。', '-', '_', '*');
    }

    private static string PickUsefulPastCaseSentence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return PickUsefulPastCaseSentence(value, requireActionSignal: false);
    }

    private static string PickUsefulPastCaseSentence(string value, bool requireActionSignal)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidates = SentenceSeparatorRegex()
            .Split(value)
            .Select(static candidate => candidate.Trim(' ', '、', '。', '-', '_', '*'))
            .Where(static candidate => candidate.Length >= 12)
            .Where(static candidate => !LooksLikeAdministrativePastCaseText(candidate))
            .Where(candidate => !requireActionSignal || HasPastCaseActionSignal(candidate))
            .OrderByDescending(ScorePastCaseSentence)
            .ThenBy(static candidate => candidate.Length)
            .ToList();

        return candidates.FirstOrDefault() ?? string.Empty;
    }

    private static bool LooksLikeAdministrativePastCaseText(string value)
    {
        return value.Contains("いつもお世話になっております", StringComparison.Ordinal) ||
            value.Contains("よろしくお願いいたします", StringComparison.Ordinal) ||
            value.Contains("技術サポート担当", StringComparison.Ordinal) ||
            value.Contains("E-Mail", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("受付", StringComparison.Ordinal) ||
            value.Contains("件名", StringComparison.Ordinal);
    }

    private static bool HasHighConfidencePastCaseActionEvidence(IReadOnlyList<SearchSource> sources)
    {
        return sources.Any(static source =>
            IsPastCaseSourceType(source.SourceType) &&
            (source.Score ?? 0) >= 0.65 &&
            HasPastCaseActionSignal(source.Text));
    }

    private static bool HasPastCaseActionSignal(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("対応内容", StringComparison.Ordinal) ||
            value.Contains("回答内容", StringComparison.Ordinal) ||
            value.Contains("回答案", StringComparison.Ordinal) ||
            value.Contains("返信案", StringComparison.Ordinal) ||
            value.Contains("確認結果", StringComparison.Ordinal) ||
            value.Contains("対応済", StringComparison.Ordinal) ||
            value.Contains("対応しました", StringComparison.Ordinal) ||
            value.Contains("送付", StringComparison.Ordinal) ||
            value.Contains("添付", StringComparison.Ordinal) ||
            value.Contains("アップロード", StringComparison.Ordinal) ||
            value.Contains("キュー追加", StringComparison.Ordinal) ||
            value.Contains("ご案内", StringComparison.Ordinal) ||
            value.Contains("案内しました", StringComparison.Ordinal) ||
            value.Contains("解決", StringComparison.Ordinal) ||
            value.Contains("クローズ", StringComparison.Ordinal) ||
            value.Contains(".zip", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScorePastCaseSentence(string value)
    {
        var score = Math.Min(value.Length, 120);
        if (value.Contains("対応", StringComparison.Ordinal) ||
            value.Contains("回避", StringComparison.Ordinal) ||
            value.Contains("設定", StringComparison.Ordinal) ||
            value.Contains("確認", StringComparison.Ordinal) ||
            value.Contains("原因", StringComparison.Ordinal))
        {
            score += 40;
        }

        if (value.Contains("QAC", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("CxSAST", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("hotfix", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("EP", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("HF", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("コンパイラ", StringComparison.Ordinal))
        {
            score += 35;
        }

        return score;
    }

    private static bool LooksLikeSupportedOsQuestion(string inquiryText)
    {
        return inquiryText.Contains("対応OS", StringComparison.OrdinalIgnoreCase) ||
            inquiryText.Contains("サポートOS", StringComparison.OrdinalIgnoreCase) ||
            inquiryText.Contains("対応オーエス", StringComparison.OrdinalIgnoreCase) ||
            SupportedOsQuestionRegex().IsMatch(inquiryText);
    }

    private static string BuildInquirySubject(AnswerDraftRequest request)
    {
        if (LooksLikeSupportedOsQuestion(request.InquiryText))
        {
            return "対応OS";
        }

        if (request.InquiryText.Contains("Dashboard", StringComparison.OrdinalIgnoreCase) ||
            request.InquiryText.Contains("ダッシュボード", StringComparison.OrdinalIgnoreCase))
        {
            return request.InquiryText.Contains("手順書", StringComparison.Ordinal)
                ? "Dashboard利用手順書"
                : "Dashboard";
        }

        var subject = request.InquiryFocus?.ImportantTerms
            .Select(static term => term.Trim())
            .FirstOrDefault(IsStrongSubjectTerm);
        return string.IsNullOrWhiteSpace(subject)
            ? "お問い合わせ内容"
            : subject;
    }

    private static bool IsStrongSubjectTerm(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Length < 3)
        {
            return false;
        }

        if (IsGenericRelevanceTerm(value))
        {
            return false;
        }

        return value.Any(char.IsLetterOrDigit) || ContainsJapanese(value);
    }

    private static bool IsEvidenceRelevantToInquiry(AnswerDraftRequest request, EvidenceItem item)
    {
        var terms = BuildEvidenceRelevanceTerms(request);
        if (terms.Count == 0)
        {
            return true;
        }

        var haystack = NormalizeForEvidenceMatch($"{item.Title} {item.Excerpt}");
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        var matchedTerms = terms
            .Where(term => haystack.Contains(NormalizeForEvidenceMatch(term), StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matchedTerms.Count == 0)
        {
            return false;
        }

        // Do not let two generic overlaps (for example, company/ownership wording)
        // make an unrelated manual look relevant when the inquiry has a concrete
        // product, feature, command, or error term to verify.
        var hasStrongQueryTerm = terms.Any(IsStrongEvidenceTerm);
        return hasStrongQueryTerm
            ? matchedTerms.Any(IsStrongEvidenceTerm)
            : matchedTerms.Count >= 2;
    }

    private static IReadOnlyList<string> BuildEvidenceRelevanceTerms(AnswerDraftRequest request)
    {
        var terms = new List<string>();
        if (request.InquiryFocus is not null)
        {
            terms.AddRange(request.InquiryFocus.TargetVersions);
            terms.AddRange(request.InquiryFocus.ImportantTerms);
        }

        terms.AddRange(SplitEvidenceRelevanceTerms(request.InquiryFocus?.FocusText ?? request.InquiryText));
        return terms
            .Select(static term => term.Trim())
            .Where(static term => term.Length >= 2)
            .Where(static term => !IsGenericRelevanceTerm(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
    }

    private static IEnumerable<string> SplitEvidenceRelevanceTerms(string value)
    {
        foreach (var raw in EvidenceTermSeparatorRegex().Split(value.Normalize(NormalizationForm.FormKC)))
        {
            var term = raw.Trim();
            if (term.Length < 2 || IsGenericRelevanceTerm(term))
            {
                continue;
            }

            yield return term;
            if (ContainsJapanese(term) && term.Length <= 40)
            {
                foreach (var ngram in CreateEvidenceJapaneseNGrams(term))
                {
                    if (!IsGenericRelevanceTerm(ngram))
                    {
                        yield return ngram;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> CreateEvidenceJapaneseNGrams(string term)
    {
        for (var length = 2; length <= Math.Min(8, term.Length); length++)
        {
            for (var start = 0; start <= term.Length - length; start++)
            {
                yield return term.Substring(start, length);
            }
        }
    }

    private static string NormalizeForEvidenceMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsStrongEvidenceTerm(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsGenericRelevanceTerm(value))
        {
            return false;
        }

        var normalized = NormalizeForEvidenceMatch(value);
        if (normalized.Length >= 5)
        {
            return true;
        }

        return normalized is "qac" or "dashboard" or "ダッシュボード" or "手順書" or "対応os" or "サポートos";
    }

    private static bool IsGenericRelevanceTerm(string value)
    {
        var normalized = NormalizeForEvidenceMatch(value);
        return normalized.Length == 0 ||
            normalized is "お願い" or "お願いします" or "ください" or "いただけ" or "いただけないか" or
                "確認" or "情報" or "具体的" or "今回" or "現在" or "提供" or "連絡" or "お手数" or
                "以上" or "よろしく" or "いつも" or "お世話" or "担当者様" or "株式会社" or "テクニカルサポート";
    }

    private static bool ContainsJapanese(string value)
    {
        return value.Any(static ch =>
            (ch >= '\u3040' && ch <= '\u30FF') ||
            (ch >= '\u3400' && ch <= '\u9FFF'));
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

    private static string EnsureCustomerReplyEmailHeader(AnswerDraftRequest request, string customerReply)
    {
        if (string.IsNullOrWhiteSpace(customerReply))
        {
            return string.Empty;
        }

        var recipientLines = BuildRecipientHeaderLines(request.Case).ToList();
        if (recipientLines.Count == 0)
        {
            return customerReply;
        }

        var normalizedReply = customerReply.TrimStart();
        var firstLines = normalizedReply
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(4)
            .ToList();

        if (recipientLines.All(line => firstLines.Any(first => string.Equals(first, line, StringComparison.Ordinal))))
        {
            return customerReply;
        }

        return string.Join(Environment.NewLine, recipientLines) +
            Environment.NewLine +
            Environment.NewLine +
            normalizedReply;
    }

    private static IEnumerable<string> BuildRecipientHeaderLines(CaseContext context)
    {
        var companyName = NormalizeRecipientText(context.CompanyName);
        if (IsPlaceholderCompanyName(companyName))
        {
            companyName = "[会社名]";
        }

        var customerName = NormalizeRecipientText(context.CustomerName);
        yield return companyName;

        if (!string.IsNullOrWhiteSpace(customerName))
        {
            yield return HasHonorificSuffix(customerName) ? customerName : $"{customerName} 様";
        }
        else
        {
            yield return "[お客様名] 様";
        }
    }

    private static string NormalizeRecipientText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NormalizeWhitespace(value).Trim(' ', '　');
    }

    private static bool IsPlaceholderCompanyName(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "株式会社サンプル", StringComparison.Ordinal) ||
            string.Equals(value, "サンプル", StringComparison.Ordinal) ||
            string.Equals(value, "TOYO", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "TOYO Corporation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "東陽テクニカ", StringComparison.Ordinal) ||
            string.Equals(value, "株式会社東陽テクニカ", StringComparison.Ordinal);
    }

    private static bool HasHonorificSuffix(string value)
    {
        return value.EndsWith("様", StringComparison.Ordinal) ||
            value.EndsWith("御中", StringComparison.Ordinal) ||
            value.EndsWith("殿", StringComparison.Ordinal);
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

    private static bool ContainsCustomerContextLeakage(string value) =>
        ContainsAny(
            value,
            "共有いただいた", "お客様環境では", "本件", "当該案件", "追記部",
            "お客様ご相談内容", "お客様への返信案", "Helix_Generic_C.cct", "default.acf");

    private static bool ContainsUnresolvedRequiredContent(AnswerDraftRequest request, string value)
    {
        if (!IsHowToQuestion(request) && !RequiresAtomicCliCommand(request))
        {
            return false;
        }

        return ContainsAny(value, "確認できません", "根拠からは確認できません");
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
        var subject = BuildInquirySubject(request);

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

    [GeneratedRegex(@"\*{3,}|-{3,}|={3,}", RegexOptions.CultureInvariant)]
    private static partial Regex PastCaseSeparatorRegex();

    [GeneratedRegex(@"追記部[_\s]*\d{4}/\d{1,2}/\d{1,2}[^\s。]*|\d{4}/\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2}[^\s。]*", RegexOptions.CultureInvariant)]
    private static partial Regex PastCaseHeaderRegex();

    [GeneratedRegex(@"[。．!！?？]\s*|\s{2,}")]
    private static partial Regex SentenceSeparatorRegex();

    [GeneratedRegex(@"(?:対応|サポート|動作環境|稼働環境|要件|対象)\s*(?:OS|オーエス)|(?:OS|オーエス)\s*(?:対応|サポート|動作環境|稼働環境|要件|一覧|について|を|は|が|ですか|でしょうか)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedOsQuestionRegex();

    [GeneratedRegex(@"[\s\r\n\t、。．，,;；:：\[\]【】（）()<>＜＞""'`]+", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceTermSeparatorRegex();
}
