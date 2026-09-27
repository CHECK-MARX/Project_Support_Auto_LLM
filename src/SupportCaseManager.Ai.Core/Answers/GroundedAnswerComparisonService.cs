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
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);

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
        if (sources.Count == 0)
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
        if (parsed.Warnings.Count > 0 || !parsed.HasEvidenceProperty ||
            string.IsNullOrWhiteSpace(draft.CustomerReplyDraft))
        {
            return Reject(baseline, "生成結果のJSONまたは根拠参照が不足しています。");
        }

        var evidenceFreeAbstention = draft.Evidence.Count == 0;
        if (evidenceFreeAbstention && !IsEvidenceFreeAbstention(draft, request.FactResolution?.AnswerReadiness))
        {
            return Reject(baseline, "引用なし回答に断定または具体的な追加確認の不足があります。",
                draft.CustomerReplyDraft);
        }

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
            return Reject(baseline, "回答案に外部送信できない参照が含まれています。");
        }

        var allowedContext = string.Join(Environment.NewLine,
            sources.Select(static source => source.Text).Append(request.InquiryText)
                .Append(request.SupplementalContext ?? string.Empty)
                .Concat(request.Case.Notes.Where(static note => note.IsCurrent)
                    .Select(static note => note.Text))
                .Concat(request.FactResolution?.ResolvedFacts.Select(static fact => fact.Value) ?? []));
        if (!PolishedAnswerValidator.PreservesProtectedValues(
            allowedContext, baseline.CustomerReplyDraft, draft.CustomerReplyDraft, request.InquiryFocus))
        {
            return Reject(baseline, "必須値の欠落、または根拠にない技術値を検出しました。");
        }

        if ((draft.CustomerReplyDraft.Contains("正式サポート", StringComparison.OrdinalIgnoreCase) ||
             draft.CustomerReplyDraft.Contains("officially supported", StringComparison.OrdinalIgnoreCase)) &&
            !sources.Any(static source =>
                source.Text.Contains("正式サポート", StringComparison.OrdinalIgnoreCase) ||
                source.Text.Contains("officially supported", StringComparison.OrdinalIgnoreCase)))
        {
            return Reject(baseline, "動作実績から正式サポートを推定した可能性があります。");
        }

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
            citationTraces.Any(trace => !candidate.Evidence.Any(item =>
                Sha256(item.SourceId) == trace.SourceIdSha256 &&
                Sha256(item.Excerpt) == trace.ExcerptSha256)))
        {
            return Reject(baseline, "後処理後の引用を元のEvidence spanへ追跡できません。",
                draft.CustomerReplyDraft, draft.Evidence.Count, citationTraces);
        }
        if (!PolishedAnswerValidator.PreservesProtectedValues(
            allowedContext, baseline.CustomerReplyDraft, candidate.CustomerReplyDraft, request.InquiryFocus))
        {
            return Reject(baseline, "後処理後の回答で必須値または技術値の整合が崩れました。");
        }

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
        var substantive = shadowReview is null || HasSubstantiveResponse(
            request.InquiryText, candidate.CustomerReplyDraft, shadowReview, expectedCoverage ?? 0);
        var safeAbstention = shadowReview is not null && substantive &&
            (candidate.CustomerReplyDraft.Contains("確認できません", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("断定できません", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("判断できません", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("不明", StringComparison.Ordinal) ||
             candidate.CustomerReplyDraft.Contains("見解が必要", StringComparison.Ordinal));
        if (safeAbstention &&
            referenceReadiness is AnswerReadiness.InsufficientEvidence or AnswerReadiness.Blocked &&
            string.Equals(candidate.Readiness, AnswerReadiness.NeedsReview, StringComparison.Ordinal))
        {
            candidate = candidate with { Readiness = referenceReadiness };
        }
        if (!string.Equals(referenceReadiness, AnswerReadiness.CustomerReady, StringComparison.Ordinal) &&
            string.Equals(candidate.Readiness, AnswerReadiness.CustomerReady, StringComparison.Ordinal))
        {
            reasons.Add("同じ根拠でReadinessが自動的に上がりました。");
        }
        if (shadowReview is not null &&
            !string.Equals(referenceReadiness, AnswerReadiness.CustomerReady, StringComparison.Ordinal) &&
            !HasConcreteCustomerAction(candidate.CustomerReplyDraft))
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
            GeneratedCitationCount = draft.Evidence.Count,
            GeneratedReplyDraft = draft.CustomerReplyDraft,
            ExpectedClaimsCoverage = expectedCoverage,
        };
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

    private static AnswerDraftResult CompleteEvidenceFreeAbstention(AnswerDraftResult draft)
    {
        if (draft.Evidence.Count != 0 || draft.NeedConfirmations.Count == 0 ||
            draft.CustomerReplyDraft.Contains("追加で必要な確認", StringComparison.Ordinal))
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

    private static bool HasConcreteCustomerAction(string reply) =>
        Regex.IsMatch(reply, @"(?:教えてください|ご教示ください|確認してください|共有してください|お知らせください|お送りください|ご提供ください|明示してください|ご確認をお願いします|メーカー.{0,12}確認)");

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
            text, @"[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)+|[A-Za-z]*[A-Z][a-z]+[A-Z][A-Za-z]*|(?<![A-Za-z0-9])(?:MQ|VPN|RLM|Validate)(?![A-Za-z0-9])")
        .Select(static match => match.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

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
