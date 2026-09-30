using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Core.Answers;

/// <summary>
/// Produces a grounded candidate for a controlled comparison. It never replaces
/// the existing answer or changes the application's answer routing.
/// </summary>
public sealed class GroundedAnswerComparisonService(
    ILlmClient llmClient,
    ISafetyRedactionService safetyRedactionService,
    IOllamaConnectionChecker? capabilityChecker = null,
    int? comparisonTimeoutOverrideSeconds = null)
{
    public async Task<GroundedAnswerComparisonResult> CompareAsync(
        AnswerDraftRequest request,
        AnswerDraftResult baseline,
        CancellationToken cancellationToken = default,
        ShadowReviewCriteria? shadowReview = null)
    {
        var first = await CompareAttemptAsync(request, baseline, cancellationToken, shadowReview, null);
        if (first.Status != GroundedComparisonStatuses.Rejected ||
            string.IsNullOrWhiteSpace(first.GeneratedReplyDraft))
            return first;

        // A validation failure may be corrected once. The second answer enters the same
        // complete parser, safety, citation, readiness and quality pipeline as the first.
        var feedback = BuildRetryFeedback(request, first);
        return await CompareAttemptAsync(request, baseline, cancellationToken, shadowReview, feedback);
    }

    private async Task<GroundedAnswerComparisonResult> CompareAttemptAsync(
        AnswerDraftRequest request,
        AnswerDraftResult baseline,
        CancellationToken cancellationToken,
        ShadowReviewCriteria? shadowReview,
        string? retryFeedback)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);
        if (request.FactResolution is { } factResolution)
            request = request with { FactResolution = ImportantFactContract.Enrich(
                factResolution, request.InquiryText, request.InquiryFocus, safetyRedactionService) };

        if (!string.Equals(request.Settings.LlmProvider.Provider, "Ollama", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(request.Settings.LlmProvider.Endpoint, UriKind.Absolute, out var endpoint) ||
            !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https"))
        {
            return Reject(baseline, "根拠付き比較はローカルOllamaに限定しています。");
        }

        var capability = await (capabilityChecker ?? new OllamaConnectionChecker())
            .CheckChatModelCapabilityAsync(request.Settings.LlmProvider, cancellationToken);
        if (!capability.CanGenerate)
        {
            return Reject(baseline, string.IsNullOrWhiteSpace(capability.Message)
                ? "回答モデルの生成能力を確認できません。"
                : capability.Message);
        }

        var evidenceIds = baseline.Evidence.Select(static item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var sources = request.Sources
            .Where(source => evidenceIds.Contains(source.SourceId) && !string.IsNullOrWhiteSpace(source.Text))
            .DistinctBy(static source => source.SourceId)
            .ToList();
        if (sources.Count == 0 && (shadowReview is null ||
            request.FactResolution?.AnswerReadiness is not (AnswerReadiness.NeedsReview or
                AnswerReadiness.InsufficientEvidence or AnswerReadiness.NeedsManufacturerConfirmation or
                AnswerReadiness.Blocked)))
        {
            return Reject(baseline, "比較に使える同一根拠がありません。");
        }

        if (shadowReview is not null &&
            !string.Equals(request.FactResolution?.AnswerReadiness, shadowReview.Readiness,
                StringComparison.Ordinal))
        {
            return Reject(baseline, "一次ReadinessとFactResolutionの生成契約が一致しません。");
        }
        PromptMessages prompt;
        try
        {
            prompt = GroundedAnswerPromptBuilder.Build(request, sources);
            if (retryFeedback is not null)
            {
                var nextLength = prompt.Diagnostics.FinalPromptChars + retryFeedback.Length;
                if (nextLength > request.Settings.MaxPromptChars)
                    return Reject(baseline, "再生成の修正指示を入力上限へ収められません。");
                prompt = prompt with
                {
                    UserPrompt = prompt.UserPrompt + retryFeedback,
                    Diagnostics = prompt.Diagnostics with
                    {
                        FinalPromptChars = nextLength,
                        UserPromptChars = prompt.Diagnostics.UserPromptChars + retryFeedback.Length,
                    },
                };
            }
        }
        catch (GroundedPromptTooLongException)
        {
            return Reject(baseline, "質問・現在案件・根拠を切り捨てずに入力上限へ収められません。");
        }

        LlmGenerationResult generation;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = comparisonTimeoutOverrideSeconds is > 0
            ? Math.Clamp(comparisonTimeoutOverrideSeconds.Value, 1, 300)
            : Math.Clamp(request.Settings.LlmProvider.TimeoutSeconds, 1, 60);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            generation = await llmClient.GenerateAsync(
                prompt, request.Settings.LlmProvider, request.Settings.DisableThinking, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Reject(baseline, "根拠付き生成が設定時間内に完了しませんでした。");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(baseline, "根拠付き生成が設定時間内に完了しませんでした。");
        }
        catch (Exception ex)
        {
            return Reject(baseline, $"根拠付き生成を完了できませんでした ({ex.GetType().Name})。");
        }

        if (string.Equals(generation.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(baseline, "生成結果が出力トークン上限に達しました。");
        }

        if (!HasExpectedJsonShape(generation.Content))
        {
            return Reject(baseline, "生成結果が要求したJSON形式または必須フィールドに一致しません。");
        }

        var parsed = AnswerDraftResultParser.Parse(generation.Content, sources);
        var draft = CompleteEvidenceFreeAbstention(parsed.Result);
        draft = draft with { CustomerReplyDraft = sources.Count > 0
            ? ScopeUnverifiedDocumentAbsence(NaturalizeCaseAttribution(draft.CustomerReplyDraft))
            : NaturalizeCaseAttribution(draft.CustomerReplyDraft) };
        draft = draft with { CustomerReplyDraft =
            PolishedAnswerValidator.RestoreUnambiguousInquiryHeader(
                draft.CustomerReplyDraft, request.InquiryText) };
        if (parsed.Warnings.Count > 0 || !parsed.HasEvidenceProperty ||
            string.IsNullOrWhiteSpace(draft.CustomerReplyDraft))
        {
            return Reject(baseline, "生成結果のJSONまたは根拠参照が不足しています。",
                draft.CustomerReplyDraft);
        }

        if (IsHeadingOnlyReply(draft.CustomerReplyDraft))
            return Reject(baseline, "顧客向け本文が見出しだけで、具体的な回答がありません。",
                draft.CustomerReplyDraft);

        var generatedCitationCount = draft.Evidence.Count;
        if (sources.Count == 0 && HasUnattributedDocumentClaim(draft.CustomerReplyDraft))
            return Reject(baseline, "検索Evidenceなしで資料の記載を確認済み事実として扱っています。",
                draft.CustomerReplyDraft);
        if (sources.Count == 0 && ReversesReportedNegation(request.InquiryText, draft.CustomerReplyDraft))
            return Reject(baseline, "顧客申告の否定表現を肯定へ反転しています。",
                draft.CustomerReplyDraft);
        if (ReversesReportedStartupFailure(request.InquiryText, draft.CustomerReplyDraft))
            return Reject(baseline, "顧客が報告した起動失敗を稼働中の事実へ反転しています。",
                draft.CustomerReplyDraft);
        if (Regex.IsMatch(draft.CustomerReplyDraft,
                @"(?:メーカー|公式|製品)資料(?:には|に).{0,40}(?:記載がない|記載されていない)"))
            return Reject(baseline, "選択資料だけから資料全体に記載がないと断定しています。",
                draft.CustomerReplyDraft);
        var deniedHeading = Regex.Match(draft.CustomerReplyDraft,
            @"[「『](?<heading>[^」』]{4,80})[」』].{0,30}(?:存在しない|存在せず|記載されていない|削除され)");
        if (deniedHeading.Success && sources.Any(source => source.SourceType == "OfficialDoc" &&
            source.Text.Contains(deniedHeading.Groups["heading"].Value, StringComparison.OrdinalIgnoreCase)))
            return Reject(baseline, "選択した公式資料に実在する見出しを不存在としています。",
                draft.CustomerReplyDraft);

        var missingInquiryVersions = (request.InquiryFocus?.TargetVersions ?? [])
            .Where(version => baseline.CustomerReplyDraft.Contains(version, StringComparison.Ordinal) &&
                !draft.CustomerReplyDraft.Contains(version, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (missingInquiryVersions.Length > 0 &&
            draft.CustomerReplyDraft.Contains("確認できる事実:", StringComparison.Ordinal))
        {
            var versionContext = GroundedAnswerPromptBuilder.DescribeVersionRoles(request.InquiryText);
            var missingDescriptions = missingInquiryVersions.Select(version =>
            {
                var index = request.InquiryText.IndexOf(version, StringComparison.Ordinal);
                var preceding = index < 0 ? string.Empty : request.InquiryText[
                    Math.Max(0, index - 20)..index];
                return Regex.IsMatch(preceding, @"以前|過去|旧版|比較")
                    ? $"過去比較版={version}"
                    : $"記載版={version}";
            });
            draft = draft with
            {
                CustomerReplyDraft = draft.CustomerReplyDraft.Replace("確認できる事実:",
                    "確認できる事実: " + (versionContext ?? string.Join("、", missingDescriptions)) + "。",
                    StringComparison.Ordinal),
                Warnings = draft.Warnings.Concat(["問い合わせと従来回答に共通する版表記を保持しました。"])
                    .ToArray(),
            };
        }

        draft = PreservePriorCaseOutcome(draft, request.FactResolution, safetyRedactionService);
        draft = PreserveSelectedOfficialTiming(draft, request.FactResolution, sources,
            safetyRedactionService);
        if (ContradictsSelectedOfficialFact(draft.CustomerReplyDraft, request.FactResolution, sources))
            return Reject(baseline, "選択した公式資料の版・見出し・確認時点と回答本文が矛盾しています。",
                draft.CustomerReplyDraft);
        if (ImportantFactContract.Validate(request.FactResolution, draft.CustomerReplyDraft) is { } factIssue)
            return Reject(baseline, factIssue, draft.CustomerReplyDraft);

        var sourceMap = sources.ToDictionary(static source => source.SourceId, StringComparer.Ordinal);
        var citationTraces = TraceCitations(draft.Evidence, sourceMap);
        if (citationTraces.Count != draft.Evidence.Count)
        {
            return Reject(baseline, "生成された引用を元の根拠本文へ追跡できません。",
                draft.CustomerReplyDraft, draft.Evidence.Count, citationTraces);
        }
        if (!string.Equals(
            safetyRedactionService.RemoveInternalReferencesFromCustomerReply(draft.CustomerReplyDraft),
            draft.CustomerReplyDraft, StringComparison.Ordinal))
        {
            return Reject(baseline, "回答案に外部送信できない参照が含まれています。",
                draft.CustomerReplyDraft);
        }

        var allowedContext = string.Join(Environment.NewLine,
            sources.Select(static source => source.Text).Append(request.InquiryText)
                .Append(request.SupplementalContext ?? string.Empty)
                .Concat(request.Case.Notes.Where(static note => note.IsCurrent)
                    .Select(static note => note.Text))
                .Concat(request.FactResolution?.ResolvedFacts.Select(static fact => fact.Value) ?? []));
        if (!PolishedAnswerValidator.PreservesProtectedValues(
            allowedContext, baseline.CustomerReplyDraft, draft.CustomerReplyDraft, request.InquiryFocus,
            request.InquiryText))
        {
            return Reject(baseline, "必須値の欠落、または根拠にない技術値を検出しました。",
                draft.CustomerReplyDraft);
        }

        if ((Regex.IsMatch(draft.CustomerReplyDraft,
                 @"正式サポート(?:対象)?(?:です|されています|である|となります|が確認されています)") ||
             Regex.IsMatch(draft.CustomerReplyDraft,
                 @"\bis officially supported\b",
                 RegexOptions.IgnoreCase)) &&
            !sources.Any(static source =>
                source.Text.Contains("正式サポート", StringComparison.OrdinalIgnoreCase) ||
                source.Text.Contains("officially supported", StringComparison.OrdinalIgnoreCase)))
        {
            return Reject(baseline, "動作実績から正式サポートを推定した可能性があります。",
                draft.CustomerReplyDraft);
        }

        var relevantCitationCount = draft.Evidence.Count(item =>
            sourceMap.TryGetValue(item.SourceId, out var source) &&
            !GroundedAnswerPromptBuilder.AllSourcesNotApplicable(request.InquiryText, [source]) &&
            GroundedAnswerPromptBuilder.SharesCitationSubject(request.InquiryText, item.Excerpt) &&
            GroundedAnswerPromptBuilder.SharesCitationSubject(
                draft.CustomerReplyDraft.Split("現時点で断定できない事項", 2,
                    StringSplitOptions.None)[0], item.Excerpt));
        var safeCitations = draft.Evidence.Where(item =>
            GroundedAnswerPromptBuilder.IsSafeCitationSpan(item.Excerpt, safetyRedactionService) &&
            sourceMap.TryGetValue(item.SourceId, out var source) &&
            !GroundedAnswerPromptBuilder.AllSourcesNotApplicable(request.InquiryText, [source]) &&
            GroundedAnswerPromptBuilder.SharesCitationSubject(request.InquiryText, item.Excerpt) &&
            GroundedAnswerPromptBuilder.SharesCitationSubject(
                draft.CustomerReplyDraft.Split("現時点で断定できない事項", 2,
                    StringSplitOptions.None)[0], item.Excerpt)).ToArray();
        if (safeCitations.Length != draft.Evidence.Count)
        {
            if (!IsEvidenceFreeAbstention(draft with { Evidence = [] },
                    request.FactResolution?.AnswerReadiness))
                return Reject(baseline, "引用に顧客固有情報または主張との関連性不足があります。",
                    draft.CustomerReplyDraft, generatedCitationCount, citationTraces);
            draft = draft with
            {
                Evidence = safeCitations,
                Warnings = draft.Warnings.Concat(["安全性・主張関連性を満たさない引用を回答候補から除外しました。"]).ToArray(),
            };
        }
        var retainedCitationTraces = TraceCitations(draft.Evidence, sourceMap);
        var evidenceFreeAbstention = draft.Evidence.Count == 0;
        if (evidenceFreeAbstention && !IsEvidenceFreeAbstention(draft, request.FactResolution?.AnswerReadiness))
            return Reject(baseline, "引用なし回答に断定または具体的な追加確認の不足があります。",
                draft.CustomerReplyDraft, generatedCitationCount, citationTraces);

        var comparisonRequest = request with { Sources = sources };
        var candidate = evidenceFreeAbstention
            ? draft with
            {
                Readiness = request.FactResolution!.AnswerReadiness,
                AnswerGenerationMode = AnswerGenerationModes.GroundedCandidate,
                Warnings = draft.Warnings.Concat(generation.Diagnostics).ToList(),
            }
            : AnswerPostProcessor.Process(
                comparisonRequest,
                draft with { Warnings = draft.Warnings.Concat(generation.Diagnostics).ToList() },
                draft.Evidence,
                draft.Confidence > 0 ? Math.Clamp(draft.Confidence, 0, 1) : baseline.Confidence,
                draft.Warnings) with
            {
                AnswerGenerationMode = AnswerGenerationModes.GroundedCandidate,
            };
        if (TraceCitations(candidate.Evidence, sourceMap).Count != candidate.Evidence.Count ||
            retainedCitationTraces.Any(trace => !candidate.Evidence.Any(item =>
                Sha256(item.SourceId) == trace.SourceIdSha256 &&
                Sha256(item.Excerpt) == trace.ExcerptSha256)))
        {
            return Reject(baseline, "後処理後の引用を元のEvidence spanへ追跡できません。",
                draft.CustomerReplyDraft, draft.Evidence.Count, citationTraces);
        }
        if (!PolishedAnswerValidator.PreservesProtectedValues(
            allowedContext, baseline.CustomerReplyDraft, candidate.CustomerReplyDraft, request.InquiryFocus,
            request.InquiryText))
        {
            return Reject(baseline, "後処理後の回答で必須値または技術値の整合が崩れました。",
                candidate.CustomerReplyDraft);
        }
        if (ReversesReportedStartupFailure(request.InquiryText, candidate.CustomerReplyDraft))
            return Reject(baseline, "後処理後の回答が顧客の起動失敗と矛盾しています。",
                candidate.CustomerReplyDraft);
        if (ImportantFactContract.Validate(request.FactResolution, candidate.CustomerReplyDraft) is { } finalFactIssue)
            return Reject(baseline, finalFactIssue, candidate.CustomerReplyDraft);

        var baselineQuality = EvaluateQuality(request, baseline.CustomerReplyDraft, sources);
        var quality = EvaluateQuality(request, candidate.CustomerReplyDraft, sources);
        var reasons = new List<string>();
        var rawGeneratedReadiness = candidate.Readiness;
        if ((!evidenceFreeAbstention && quality.Grounding < baselineQuality.Grounding) ||
            (!evidenceFreeAbstention && quality.TechnicalFidelity < baselineQuality.TechnicalFidelity) ||
            quality.UnsupportedClaimCount > baselineQuality.UnsupportedClaimCount)
        {
            reasons.Add("同一根拠での品質指標が従来回答より低下しました。");
        }

        if (quality.UnsupportedClaimCount > 0 || quality.InternalLeakageCount > 0 ||
            string.Equals(quality.Decision, AnswerQualityDecisions.Blocked, StringComparison.Ordinal))
        {
            reasons.Add("品質検査が未裏付けの主張または内部情報を検出しました。");
        }

        var factReadiness = request.FactResolution?.AnswerReadiness;
        var referenceReadiness = shadowReview?.Readiness ??
            (factReadiness is AnswerReadiness.NeedsReview or AnswerReadiness.NeedsConfirmation or
                AnswerReadiness.NeedsCustomerConfirmation or AnswerReadiness.NeedsManufacturerConfirmation or
                AnswerReadiness.InsufficientEvidence or AnswerReadiness.Blocked
                ? factReadiness : baseline.Readiness);
        double? expectedCoverage = shadowReview is null ? null : MeasureExpectedCoverage(
            shadowReview.ExpectedClaims, candidate.CustomerReplyDraft);
        var caseSpecificAbstention = evidenceFreeAbstention &&
            GroundedAnswerPromptBuilder.AllSourcesNotApplicable(request.InquiryText, sources) &&
            HasConcreteCustomerAction(candidate.CustomerReplyDraft, request.InquiryText) &&
            SharesInquirySubject(request.InquiryText, candidate.CustomerReplyDraft);
        var substantive = shadowReview is null || caseSpecificAbstention || HasSubstantiveResponse(
            request.InquiryText, candidate.CustomerReplyDraft, shadowReview, expectedCoverage ?? 0);
        var safeAbstention = shadowReview is not null && substantive &&
            (candidate.CustomerReplyDraft.Contains("確認できません", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("断定できません", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("判断できません", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("不明", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("見解が必要", StringComparison.Ordinal));
        if (referenceReadiness is AnswerReadiness.NeedsReview or AnswerReadiness.NeedsConfirmation or
            AnswerReadiness.NeedsCustomerConfirmation or AnswerReadiness.NeedsManufacturerConfirmation or
            AnswerReadiness.InsufficientEvidence or AnswerReadiness.Blocked &&
            !string.Equals(candidate.Readiness, referenceReadiness, StringComparison.Ordinal))
        {
            candidate = candidate with { Readiness = referenceReadiness };
            if (!IsSafeNonReadyBody(candidate.CustomerReplyDraft, request.InquiryText))
                reasons.Add("同じ根拠でReadinessが自動的に上がり、回答本文も非Readyの条件を満たしません。");
        }
        if (shadowReview is not null &&
            !string.Equals(referenceReadiness, AnswerReadiness.CustomerReady, StringComparison.Ordinal) &&
            !HasConcreteCustomerAction(candidate.CustomerReplyDraft, request.InquiryText))
        {
            reasons.Add("顧客向け本文に具体的な追加確認がありません。");
        }

        if (shadowReview is not null && !substantive)
        {
            reasons.Add("Expected Claimsまたは必要な根拠への対応がなく、質問の言い換えにとどまっています。");
        }
        if (shadowReview is not null && shadowReview.ForbiddenClaims.Any(claim =>
            IsForbiddenAssertionPresent(claim, candidate.CustomerReplyDraft)))
        {
            reasons.Add("禁止主張に該当する回答が含まれています。");
        }

        return new GroundedAnswerComparisonResult
        {
            Baseline = baseline,
            BaselineQuality = baselineQuality,
            Candidate = reasons.Count == 0 ? candidate : null,
            CandidateQuality = quality,
            PromptChars = prompt.Diagnostics.FinalPromptChars,
            Status = reasons.Count == 0 ? GroundedComparisonStatuses.ReadyForHumanReview : GroundedComparisonStatuses.Rejected,
            Reasons = reasons,
            GeneratedReadiness = candidate.Readiness,
            RawGeneratedReadiness = rawGeneratedReadiness,
            CitationTraces = citationTraces,
            CitationRelevantCount = relevantCitationCount,
            GeneratedCitationCount = generatedCitationCount,
            AcceptedCitationCount = candidate.Evidence.Count,
            GeneratedReplyDraft = draft.CustomerReplyDraft,
            ExpectedClaimsCoverage = expectedCoverage,
        };
    }

    private static string BuildRetryFeedback(AnswerDraftRequest request,
        GroundedAnswerComparisonResult rejected)
    {
        var feedback = new StringBuilder();
        feedback.AppendLine();
        feedback.AppendLine("一度目の回答は既存の検証で不合格です。今回が最後の生成です。" +
            "同じEvidence、一次Readiness、JSON SchemaでcustomerReplyDraftを全面的に書き直してください。");
        foreach (var reason in rejected.Reasons.Take(3))
            feedback.AppendLine($"検証失敗: {reason}");
        if (IsHeadingOnlyReply(rejected.GeneratedReplyDraft ?? string.Empty))
            feedback.AppendLine("3見出しは各見出しの後に顧客に伝える具体的な文章を書き、" +
                "見出し名だけを並べないでください。internalMemoやneedConfirmationsだけで代替しないでください。");
        if (request.FactResolution is { } resolution)
        {
            foreach (var fact in ImportantFactContract.Select(resolution).Take(6))
                feedback.AppendLine($"本文で保持: {ImportantFactContract.DescribeForGeneration(fact)}");
        }
        var exactValues = PolishedAnswerValidator.ExtractInquiryTechnicalValues(request.InquiryText);
        if (exactValues.Count > 0)
            feedback.AppendLine($"原文表記を厳守: {string.Join("、", exactValues.Take(8))}。" +
                "原文にない別表記を作らないでください。");
        feedback.AppendLine("前回の不合格文を転記せず、出典の帰属、事実の極性、版・操作・時点、" +
            "具体的な追加確認を保ってください。すべての検証に再び合格しなければ採用しません。");
        return feedback.ToString();
    }

    private static bool IsHeadingOnlyReply(string reply)
    {
        var body = reply;
        foreach (var heading in new[] { "確認できる事実", "現時点で断定できない事項", "追加で必要な確認" })
            body = body.Replace(heading, string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(Regex.Replace(body, @"[\s:：/／・、。;；\-]+", string.Empty));
    }

    private static IReadOnlyList<CitationTrace> TraceCitations(
        IReadOnlyList<EvidenceItem> evidence, IReadOnlyDictionary<string, SearchSource> sources)
    {
        var traces = new List<CitationTrace>();
        foreach (var item in evidence)
        {
            if (!sources.TryGetValue(item.SourceId, out var source) ||
                string.IsNullOrWhiteSpace(item.Excerpt) ||
                !string.Equals(item.SourceType, source.SourceType, StringComparison.Ordinal)) break;
            var start = source.Text.IndexOf(item.Excerpt, StringComparison.Ordinal);
            if (start < 0) break;
            traces.Add(new CitationTrace(Sha256(source.SourceId), Sha256(source.Text),
                source.SourceType, start, item.Excerpt.Length, Sha256(item.Excerpt)));
        }
        return traces;
    }

    private static string NaturalizeCaseAttribution(string reply) => Regex.Replace(reply,
        @"[（(]\s*sourceId\s*[:=]\s*(?<id>inquiry|case:[\w:.-]+)\s*[）)]",
        match => string.Equals(match.Groups["id"].Value, "inquiry", StringComparison.OrdinalIgnoreCase)
            ? "（お客様のご説明では）" : "（案件履歴によると）",
        RegexOptions.IgnoreCase);

    private static string ScopeUnverifiedDocumentAbsence(string reply) => Regex.Replace(reply,
        @"(?:公式|メーカー|製品)資料(?:には|に)(?:その)?記載(?:が)?ありません",
        "今回選択した資料では確認できません");

    private static AnswerDraftResult PreservePriorCaseOutcome(AnswerDraftResult draft,
        FactResolutionResult? resolution, ISafetyRedactionService redactor)
    {
        var facts = resolution?.ResolvedFacts.Where(fact =>
            fact.Key == "PriorCaseOutcome" && fact.SourceType == "CurrentCase" &&
            fact.Status == FactStatuses.Candidate &&
            Regex.IsMatch(fact.Value, @"[ぁ-んァ-ン一-龥]") &&
            fact.Value.Length <= 200 &&
            string.Equals(redactor.RedactForCloud(fact.Value), fact.Value, StringComparison.Ordinal)).ToArray();
        if (facts is null || facts.Length == 0 ||
            !draft.CustomerReplyDraft.Contains("確認できる事実:", StringComparison.Ordinal))
            return draft;
        var reply = draft.CustomerReplyDraft;
        foreach (var fact in facts)
        {
            if (ImportantFactContract.IsPriorCaseOutcomePreserved(fact, reply)) continue;
            var observation = fact.Value.Trim().TrimStart('-', ' ', '　');
            reply = reply.Replace("確認できる事実:",
                $"確認できる事実: 案件履歴には、{observation}（当時の観測であり、今回の原因や恒久対策は未確認です。）",
                StringComparison.Ordinal);
        }
        return draft with { CustomerReplyDraft = reply };
    }

    private static bool ContradictsSelectedOfficialFact(string reply,
        FactResolutionResult? resolution, IReadOnlyList<SearchSource> sources)
    {
        if (resolution is null) return false;
        foreach (var fact in resolution.ResolvedFacts.Where(fact =>
            fact.Key == "SelectedOfficialStatement" && fact.SourceType == "OfficialDoc" &&
            fact.Status == FactStatuses.Confirmed))
        {
            var source = sources.FirstOrDefault(source => source.SourceId == fact.EvidenceId &&
                source.SourceType == "OfficialDoc");
            if (source is null) continue;
            var sourceVersion = Regex.Match(source.Text,
                @"\b(?<version>20\d{2}\.\d+)\s+Release\s+notes\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var heading = Regex.Match(fact.Statement, @"見出し『(?<heading>[^』]+)』");
            if (!sourceVersion.Success || !heading.Success) continue;
            var version = sourceVersion.Groups["version"].Value;
            var title = Regex.Escape(heading.Groups["heading"].Value);
            foreach (Match attribution in Regex.Matches(reply,
                @"(?<version>20\d{2}\.\d+)(?:の|版の)?(?:リリースノート|Release\s+notes).{0,35}[「『]" +
                title + @"[」』]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (attribution.Groups["version"].Value != version &&
                    !sources.Any(other => other.SourceType == "OfficialDoc" &&
                        other.Text.Contains(attribution.Groups["version"].Value + " Release notes",
                            StringComparison.OrdinalIgnoreCase) &&
                        other.Text.Contains(heading.Groups["heading"].Value, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            if (Regex.IsMatch(reply,
                Regex.Escape(version) + @".{0,55}[「『]" + title +
                @"[」』].{0,30}(?:記載(?:が)?(?:ない|見られず|されていない)|存在しない|削除され)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
            if (Regex.IsMatch(fact.Value, @"\bbefore\s+(?:your|the)\s+first\s+integration\s+build",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !Regex.IsMatch(reply, @"初回.{0,35}(?:integration|インテグレーション|統合).{0,20}(?:build|ビルド).{0,15}前|before\s+(?:your|the)\s+first\s+integration\s+build",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }
        return false;
    }

    private static AnswerDraftResult PreserveSelectedOfficialTiming(AnswerDraftResult draft,
        FactResolutionResult? resolution, IReadOnlyList<SearchSource> sources,
        ISafetyRedactionService redactor)
    {
        if (resolution is null || !draft.CustomerReplyDraft.Contains("確認できる事実:", StringComparison.Ordinal) ||
            !draft.CustomerReplyDraft.Contains("現時点で断定できない事項", StringComparison.Ordinal))
            return draft;
        var reply = draft.CustomerReplyDraft;
        foreach (var fact in resolution.ResolvedFacts.Where(fact =>
            fact.Key == "SelectedOfficialStatement" && fact.Status == FactStatuses.Confirmed &&
            fact.SourceType == "OfficialDoc" && sources.Any(source =>
                source.SourceId == fact.EvidenceId && source.SourceType == "OfficialDoc" &&
                source.Text.Contains(fact.Value, StringComparison.Ordinal))))
        {
            var timing = Regex.Match(fact.Value,
                @"\bbefore\s+(?:your|the)\s+first\s+integration\s+build",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!timing.Success || fact.Value.Length > 280 ||
                !string.Equals(redactor.RedactForCloud(fact.Value), fact.Value, StringComparison.Ordinal) ||
                Regex.IsMatch(reply, @"初回.{0,35}(?:integration|インテグレーション|統合).{0,20}(?:build|ビルド).{0,15}前|before\s+(?:your|the)\s+first\s+integration\s+build",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;
            var version = Regex.Match(sources.First(source => source.SourceId == fact.EvidenceId).Text,
                @"\b(?<version>20\d{2}\.\d+)\s+Release\s+notes\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!version.Success) continue;
            reply = reply.Replace("現時点で断定できない事項",
                $"選択した{version.Groups["version"].Value}版の公式資料には「{fact.Value.Trim()}」と記載されています。" +
                "現時点で断定できない事項", StringComparison.Ordinal);
        }
        return draft with { CustomerReplyDraft = reply };
    }

    private static bool IsSafeNonReadyBody(string reply, string inquiry) =>
        reply.Contains("断定できない事項", StringComparison.Ordinal) &&
        HasConcreteCustomerAction(reply, inquiry) &&
        (reply.Contains("確認できません", StringComparison.Ordinal) ||
         reply.Contains("断定できません", StringComparison.Ordinal) ||
         reply.Contains("確認できない", StringComparison.Ordinal) ||
         reply.Contains("情報がありません", StringComparison.Ordinal) ||
         reply.Contains("不明", StringComparison.Ordinal) ||
         reply.Contains("見解が必要", StringComparison.Ordinal));

    private static bool IsEvidenceFreeAbstention(AnswerDraftResult draft, string? readiness)
    {
        if (readiness is not (AnswerReadiness.NeedsReview or AnswerReadiness.InsufficientEvidence or
            AnswerReadiness.NeedsManufacturerConfirmation or AnswerReadiness.Blocked) ||
            draft.NeedConfirmations.Count == 0)
            return false;

        var reply = draft.CustomerReplyDraft;
        return reply.Contains("確認できる事実", StringComparison.Ordinal) &&
            reply.Contains("断定できない事項", StringComparison.Ordinal) &&
            reply.Contains("追加で必要な確認", StringComparison.Ordinal) &&
            !Regex.IsMatch(reply, @"(?:利用|運用|対応|解消|解決)(?:が|は|できます|可能です|しました).{0,8}(?:可能です|できます|完了しました|解決しました)");
    }

    private static bool HasUnattributedDocumentClaim(string reply)
    {
        var factSection = reply.Split("現時点で断定できない事項", 2, StringSplitOptions.None)[0];
        return Regex.IsMatch(factSection,
                @"(?:ページ|資料|マニュアル|リリースノート|公式文書).{0,80}(?:記載|明記|示され)") &&
            !Regex.IsMatch(factSection,
                @"(?:お客様|顧客|ご提示|相談内容|申告|案件履歴).{0,80}(?:によると|では|として|が報告した|からの報告)");
    }

    private static bool ReversesReportedNegation(string inquiry, string reply)
    {
        var factSection = Regex.Replace(
            reply.Split("現時点で断定できない事項", 2, StringSplitOptions.None)[0],
            @"\s+", string.Empty);
        foreach (Match match in Regex.Matches(inquiry,
            @"(?<subject>[^。！？\r\n]{4,60}?)(?:は|が)(?:ありません|存在しません|ない)"))
        {
            var subject = Regex.Replace(match.Groups["subject"].Value, @"\s+", string.Empty);
            subject = Regex.Split(subject, @"[にで]").LastOrDefault(static part => part.Length >= 4) ?? subject;
            if (subject.Length > 18) subject = subject[^18..];
            var index = factSection.IndexOf(subject, StringComparison.Ordinal);
            if (index < 0) continue;
            var following = factSection[(index + subject.Length)..];
            if (!Regex.IsMatch(following, @"^.{0,16}(?:ありません|存在しません|ない|なし)"))
                return true;
        }
        return false;
    }

    private static bool ReversesReportedStartupFailure(string inquiry, string reply)
    {
        var factSection = reply.Split("現時点で断定できない事項", 2,
            StringSplitOptions.None)[0];
        foreach (Match failure in Regex.Matches(inquiry,
            @"(?<subject>[\p{L}][\p{L}0-9._-]{1,50}(?:サーバー|サービス|プロセス))\s*(?:を|が|は)?\s*(?:起動できません|起動に失敗|起動しませんでした|起動せず)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var subject = failure.Groups["subject"].Value;
            foreach (Match mention in Regex.Matches(factSection, Regex.Escape(subject),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var rest = factSection[(mention.Index + mention.Length)..];
                var clause = Regex.Split(rest, @"[。．.!?\r\n]|現時点で")[0];
                if (clause.Length > 65) clause = clause[..65];
                if (Regex.IsMatch(clause,
                        @".{0,35}(?:起動し(?:た|ている|ています|、)|起動中|稼働中|正常に起動|稼働し(?:た|ている|ています|、))") &&
                    !Regex.IsMatch(clause,
                        @".{0,35}(?:起動できない|起動できません|起動に失敗|起動せず|起動していない|起動していません)"))
                    return true;
            }
        }
        return false;
    }

    private static AnswerDraftResult CompleteEvidenceFreeAbstention(AnswerDraftResult draft)
    {
        if (draft.Evidence.Count != 0 || draft.NeedConfirmations.Count == 0)
            return draft;

        if (draft.CustomerReplyDraft.Contains("確認できる事実", StringComparison.Ordinal) &&
            draft.CustomerReplyDraft.Contains("追加で必要な確認", StringComparison.Ordinal) &&
            !draft.CustomerReplyDraft.Contains("断定できない事項", StringComparison.Ordinal))
        {
            var sections = Regex.Match(draft.CustomerReplyDraft,
                @"(?<fact>確認できる事実\s*[:：][\s\S]*?)(?<unknown>現時点で[^。\r\n]{0,160}断定できない[。.]?)\s*(?<action>追加で必要な確認\s*[:：][\s\S]+)");
            if (sections.Success)
                return draft with
                {
                    CustomerReplyDraft = sections.Groups["fact"].Value.Trim() + Environment.NewLine +
                        "現時点で断定できない事項: " + sections.Groups["unknown"].Value.Trim() + Environment.NewLine +
                        sections.Groups["action"].Value.Trim(),
                };
        }

        if (draft.CustomerReplyDraft.Contains("追加で必要な確認", StringComparison.Ordinal))
            return draft;

        var parts = draft.CustomerReplyDraft.Split(["ただし、", "一方、"],
            2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !(parts[1].Contains("情報がありません", StringComparison.Ordinal) ||
              parts[1].Contains("確認できません", StringComparison.Ordinal) ||
              parts[1].Contains("断定できません", StringComparison.Ordinal) ||
              parts[1].Contains("不明", StringComparison.Ordinal)))
            return draft;

        var questions = draft.NeedConfirmations.Select(static item => item.Question.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal).Take(3).ToArray();
        if (questions.Length == 0) return draft;
        return draft with
        {
            CustomerReplyDraft = "確認できる事実: " + parts[0].Trim() + Environment.NewLine +
                "現時点で断定できない事項: " + parts[1].Trim() + Environment.NewLine +
                "追加で必要な確認: " + string.Join(" ", questions),
            Warnings = draft.Warnings.Concat(["モデルのneedConfirmationsを顧客向け本文に反映しました。"]).ToArray(),
        };
    }

    private static bool HasConcreteCustomerAction(string reply, string? inquiry = null)
    {
        if (Regex.IsMatch(reply,
            @"(?:教えてください|ご教示ください|確認してください|共有してください|お知らせください|お送りください|ご提供ください|明示してください|ご確認をお願いします|メーカー.{0,12}確認)"))
            return true;

        var heading = Regex.Match(reply, @"追加で必要な確認\s*[:：]\s*(?<items>[\s\S]+)");
        if (!heading.Success) return false;
        var items = heading.Groups["items"].Value;
        if (Regex.IsMatch(items,
            @"(?:設定内容|設定値|参照先|稼働状態|実行時刻|関連ログ|ログファイル|対象バージョン|(?:使用|利用|対象|現在|稼働中).{0,32}バージョン|SQL Server.{0,20}(?:バージョン|構成)|ライセンス方式|通信条件|対応可否|互換性|構成差分|選定基準|検出.{0,12}コード|Azure SQL|メーカー.{0,16}(?:見解|情報)|[A-Z][A-Za-z0-9 ]{2,40}(?:確認|必要))"))
            return true;
        return !string.IsNullOrWhiteSpace(inquiry) &&
            Regex.IsMatch(items, @"(?:明記|記載|提示)してください") &&
            TechnicalAnchors(inquiry).Any(anchor =>
                items.Contains(anchor, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static double MeasureExpectedCoverage(IReadOnlyList<string> claims, string answer)
    {
        if (claims.Count == 0) return 0;
        var matched = claims.Count(claim => TechnicalAnchors(claim).Any(anchor =>
            answer.Contains(anchor, StringComparison.OrdinalIgnoreCase)));
        return (double)matched / claims.Count;
    }

    private static IReadOnlyList<string> TechnicalAnchors(string text) => Regex.Matches(
            text, @"[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)+|[A-Za-z]*[A-Z][a-z]+[A-Z][A-Za-z]*|(?<![A-Za-z0-9])(?:[A-Z]{2,}|[A-Z][a-z]{2,})(?:\s+(?:[A-Z]{2,}|[A-Z][a-z]{2,}))*(?![A-Za-z0-9])")
        .Select(static match => match.Value)
        .Where(static value => value is not ("Customer" or "Source" or "Evidence" or "Version"))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool SharesInquirySubject(string question, string answer) =>
        TechnicalAnchors(question).Any(anchor =>
            answer.Contains(anchor, StringComparison.OrdinalIgnoreCase)) ||
        Regex.Matches(question, @"[ァ-ヴー]{4,}").Select(static match => match.Value)
            .Any(term => answer.Contains(term, StringComparison.Ordinal));

    private static bool HasSubstantiveResponse(string question, string answer,
        ShadowReviewCriteria review, double coverage)
    {
        if (coverage > 0 && review.RequiredEvidence.Count > 0) return true;
        // A safe abstention names the missing technical subject; a generic request
        // for more information does not turn a paraphrase into a usable answer.
        var requiredAnchors = review.ExpectedClaims.Concat(review.RequiredEvidence)
            .SelectMany(TechnicalAnchors).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var namesGap = requiredAnchors.Any(anchor =>
            answer.Contains(anchor, StringComparison.OrdinalIgnoreCase) &&
            (answer.Contains("確認", StringComparison.Ordinal) ||
             answer.Contains("不足", StringComparison.Ordinal) ||
             answer.Contains("未確認", StringComparison.Ordinal) ||
             answer.Contains("不明", StringComparison.Ordinal) ||
             answer.Contains("必要", StringComparison.Ordinal)));
        if (!namesGap) return false;
        var normalizedQuestion = Regex.Replace(question, @"\s+", "");
        var normalizedAnswer = Regex.Replace(answer, @"\s+", "");
        return !normalizedAnswer.Contains(normalizedQuestion, StringComparison.Ordinal);
    }

    private static bool IsForbiddenAssertionPresent(string forbidden, string answer)
    {
        // Negative review instructions cannot safely be matched as literal phrases.
        // Only explicit affirmative support assertions are treated as violations here.
        return forbidden.Contains("正式サポート", StringComparison.Ordinal) &&
            Regex.IsMatch(answer, @"正式サポート(?:対象)?(?:です|されています|である|となります)");
    }

    private static AnswerQualityEvaluationResult EvaluateQuality(
        AnswerDraftRequest request,
        string answer,
        IReadOnlyList<SearchSource> sources) => AnswerQualityEvaluator.Evaluate(new AnswerQualityEvaluationInput
        {
            Question = request.InquiryText,
            Answer = answer,
            ProductName = request.Case.ProductName,
            RequestedVersion = request.InquiryFocus?.TargetVersions.FirstOrDefault(),
            Evidence = sources.Select(static source => new AnswerQualityEvidence
            {
                SourceId = source.SourceId,
                SourceType = source.SourceType,
                Text = source.Text,
                ProductName = source.ProductName,
            }).ToList(),
            Catalog = AnswerQualityEvaluator.CreateSupportCatalog(request.Case.ProductName),
            UseSeparatedCoverage = request.Settings.UsePhase175QualityControls,
            RequiredCoverage = request.InquiryFocus?.RequiredCoverage ?? [],
        });

    private static bool HasExpectedJsonShape(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !IsProperty(root, "customerReplyDraft", JsonValueKind.String) ||
                !IsProperty(root, "internalMemo", JsonValueKind.String) ||
                !IsProperty(root, "needConfirmations", JsonValueKind.Array) ||
                !IsProperty(root, "evidence", JsonValueKind.Array) ||
                !IsProperty(root, "confidence", JsonValueKind.Number) ||
                !IsProperty(root, "warnings", JsonValueKind.Array) ||
                !root.GetProperty("confidence").TryGetDouble(out var confidence) ||
                !double.IsFinite(confidence) || confidence is < 0 or > 1)
            {
                return false;
            }

            return root.GetProperty("evidence").EnumerateArray().All(static item =>
                item.ValueKind == JsonValueKind.Object &&
                IsProperty(item, "sourceId", JsonValueKind.String) &&
                IsProperty(item, "excerpt", JsonValueKind.String));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsProperty(JsonElement root, string name, JsonValueKind kind) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == kind;

    private static GroundedAnswerComparisonResult Reject(AnswerDraftResult baseline, string reason,
        string? generatedReply = null, int generatedCitationCount = 0,
        IReadOnlyList<CitationTrace>? citationTraces = null) => new()
    {
        Baseline = baseline,
        Status = GroundedComparisonStatuses.Rejected,
        Reasons = [reason],
        GeneratedReplyDraft = generatedReply,
        GeneratedCitationCount = generatedCitationCount,
        CitationTraces = citationTraces ?? [],
    };
}

public static class GroundedComparisonStatuses
{
    public const string ReadyForHumanReview = "ReadyForHumanReview";
    public const string Rejected = "Rejected";
}

public sealed record class GroundedAnswerComparisonResult
{
    public AnswerDraftResult Baseline { get; init; } = new();
    public AnswerQualityEvaluationResult? BaselineQuality { get; init; }
    public AnswerDraftResult? Candidate { get; init; }
    public AnswerQualityEvaluationResult? CandidateQuality { get; init; }
    public string Status { get; init; } = GroundedComparisonStatuses.Rejected;
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public int PromptChars { get; init; }
    public string? GeneratedReadiness { get; init; }
    public string? RawGeneratedReadiness { get; init; }
    public double? ExpectedClaimsCoverage { get; init; }
    public IReadOnlyList<CitationTrace> CitationTraces { get; init; } = [];
    public int GeneratedCitationCount { get; init; }
    public int CitationRelevantCount { get; init; }
    public int AcceptedCitationCount { get; init; }
    public string? GeneratedReplyDraft { get; init; }
}

public sealed record class ShadowReviewCriteria(
    IReadOnlyList<string> ExpectedClaims,
    IReadOnlyList<string> ForbiddenClaims,
    IReadOnlyList<string> RequiredEvidence,
    string Readiness);

public sealed record class CitationTrace(
    string SourceIdSha256, string SourceTextSha256, string SourceType,
    int SpanStart, int SpanLength, string ExcerptSha256);
