using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Core.Answers;

public sealed class AiAnswerService : IAiAnswerService
{
    private const int StandardPolishingTimeoutSeconds = 30;
    private const int QualityPolishingTimeoutSeconds = 60;
    private readonly IPromptBuilder promptBuilder;
    private readonly IEvidenceBuilder evidenceBuilder;
    private readonly ISafetyRedactionService safetyRedactionService;
    private readonly ILlmClient llmClient;

    public AiAnswerService(
        IPromptBuilder promptBuilder,
        IEvidenceBuilder evidenceBuilder,
        ISafetyRedactionService safetyRedactionService,
        ILlmClient llmClient)
    {
        this.promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        this.evidenceBuilder = evidenceBuilder ?? throw new ArgumentNullException(nameof(evidenceBuilder));
        this.safetyRedactionService = safetyRedactionService ?? throw new ArgumentNullException(nameof(safetyRedactionService));
        this.llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
    }

    public async Task<AnswerDraftResult> GenerateDraftAsync(
        AnswerDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fallbackEvidence = evidenceBuilder.BuildEvidence(request);
        var deterministic = AnswerPostProcessor.Process(
            request,
            new AnswerDraftResult
            {
                InternalMemo = "LLM実行前にEvidenceから決定論的回答を生成しました。",
                GeneratedAt = DateTimeOffset.Now,
            },
            fallbackEvidence,
            evidenceBuilder.CalculateConfidence(request, fallbackEvidence),
            request.InstructionWarnings);

        if (fallbackEvidence.Count == 0)
        {
            return deterministic with { AnswerGenerationMode = AnswerGenerationModes.DeterministicOnly };
        }

        PromptMessages promptMessages = PolisherPromptBuilder.Build(deterministic.CustomerReplyDraft, request.Settings.MaxPromptChars);
        LlmGenerationResult generation;
        using var polishingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var polishingTimeoutSeconds = EffectivePolishingTimeoutSeconds(request.Settings);
        polishingCancellation.CancelAfter(TimeSpan.FromSeconds(polishingTimeoutSeconds));
        try
        {
            generation = await llmClient.GenerateAsync(
                promptMessages,
                request.Settings.LlmProvider,
                request.Settings.DisableThinking,
                polishingCancellation.Token);
        }
        catch (OperationCanceledException) when (polishingCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return deterministic with
            {
                AnswerGenerationMode = AnswerGenerationModes.PolishingTimedOut,
                Warnings = deterministic.Warnings.Concat([$"Polishingが{polishingTimeoutSeconds}秒でタイムアウトしたため、Deterministic Answerを採用しました。"]).Distinct().ToList(),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return deterministic with
            {
                AnswerGenerationMode = AnswerGenerationModes.PolishingCancelled,
                Warnings = deterministic.Warnings.Concat(["Polishingをキャンセルしたため、Deterministic Answerを採用しました。"]).Distinct().ToList(),
            };
        }
        catch (TimeoutException exception)
        {
            return deterministic with
            {
                AnswerGenerationMode = AnswerGenerationModes.PolishingTimedOut,
                Warnings = deterministic.Warnings.Concat([$"Polishingがタイムアウトしたため、Deterministic Answerを採用しました。エラー={exception.GetType().Name}"]).Distinct().ToList(),
            };
        }
        catch (Exception exception)
        {
            return deterministic with
            {
                AnswerGenerationMode = AnswerGenerationModes.PolishingFailed,
                Warnings = deterministic.Warnings.Concat([$"Polishingを利用できないため、Deterministic Answerを採用しました。エラー={exception.GetType().Name}"]).Distinct().ToList(),
            };
        }

        var response = generation.Content;
        var parsed = AnswerDraftResultParser.Parse(response, request.Sources);
        var result = parsed.Result;
        var warnings = new List<string>();
        warnings.AddRange(request.InstructionWarnings);
        warnings.AddRange(parsed.Warnings);
        warnings.AddRange(result.Warnings);
        warnings.AddRange(generation.Diagnostics);
        warnings.AddRange(safetyRedactionService.FindCustomerReplyWarnings(result.CustomerReplyDraft));

        var safeCustomerReply = safetyRedactionService.RemoveInternalReferencesFromCustomerReply(result.CustomerReplyDraft);
        var resultEvidence = parsed.HasEvidenceProperty ? result.Evidence : fallbackEvidence;
        var confidence = result.Confidence > 0
            ? Math.Clamp(result.Confidence, 0, 1)
            : evidenceBuilder.CalculateConfidence(request, resultEvidence);

        if (request.Sources.Count == 0 && resultEvidence.Count == 0 && confidence > 0)
        {
            confidence = 0;
        }

        var processed = result with
        {
            CustomerReplyDraft = safeCustomerReply,
            Evidence = resultEvidence,
            Confidence = confidence,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            GeneratedAt = result.GeneratedAt == default ? DateTimeOffset.Now : result.GeneratedAt,
        };

        var protectedContext = deterministic.CustomerReplyDraft + Environment.NewLine +
            string.Join(Environment.NewLine, request.Sources.Select(static source => source.Text));
        if (!PolishedAnswerValidator.PreservesProtectedValues(
                protectedContext,
                processed.CustomerReplyDraft))
        {
            return deterministic with
            {
                AnswerGenerationMode = AnswerGenerationModes.PolishingFailed,
                Warnings = deterministic.Warnings.Concat(["Polished Answerの保護値検証に失敗したため、Deterministic Answerを採用しました。"]).Distinct().ToList(),
            };
        }

        var postProcessed = AnswerPostProcessor.Process(
            request,
            processed,
            resultEvidence,
            confidence,
            processed.Warnings);

        postProcessed = postProcessed with
        {
            AnswerGenerationMode = AnswerGenerationModes.DeterministicWithPolishing,
            DeterministicAnswerCreated = true,
        };

        if (!request.Settings.UseAnswerQualityGate)
        {
            return postProcessed;
        }
        var quality = AnswerQualityEvaluator.Evaluate(new AnswerQualityEvaluationInput
        {
            Question = request.InquiryText,
            Answer = postProcessed.CustomerReplyDraft,
            ProductName = request.Case.ProductName,
            RequestedVersion = request.InquiryFocus?.TargetVersions.FirstOrDefault(),
            Evidence = BuildQualityEvidence(request.Sources, postProcessed.Evidence),
            Catalog = AnswerQualityEvaluator.CreateSupportCatalog(request.Case.ProductName),
            UseSeparatedCoverage = request.Settings.UsePhase175QualityControls,
            RequiredCoverage = request.InquiryFocus?.RequiredCoverage ?? [],
        });
        var qualityWarnings = postProcessed.Warnings
            .Concat([$"Answer Quality Gate: {quality.Decision}"])
            .Concat(quality.BlockingReasons.Select(static reason =>
                $"Answer Quality blocking reason: {reason}"))
            .Concat(quality.Warnings.Select(static warning =>
                $"Answer Quality warning: {warning}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return postProcessed with
        {
            AnswerQuality = quality,
            Warnings = qualityWarnings,
        };
    }

    private static int EffectivePolishingTimeoutSeconds(AiAssistantSettings settings)
    {
        var configured = settings.LlmProvider.TimeoutSeconds > 0
            ? settings.LlmProvider.TimeoutSeconds
            : StandardPolishingTimeoutSeconds;
        var maximum = string.Equals(settings.AnswerQualityMode, AnswerQualityModes.Quality, StringComparison.OrdinalIgnoreCase)
            ? QualityPolishingTimeoutSeconds
            : StandardPolishingTimeoutSeconds;
        return Math.Min(configured, maximum);
    }

    private static IReadOnlyList<AnswerQualityEvidence> BuildQualityEvidence(
        IReadOnlyList<SearchSource> sources,
        IReadOnlyList<EvidenceItem> resultEvidence)
    {
        var values = sources
            .Where(static source => !string.IsNullOrWhiteSpace(source.Text))
            .Select(static source => new AnswerQualityEvidence
            {
                SourceId = source.SourceId,
                SourceType = source.SourceType,
                Text = source.Text,
                ProductName = source.ProductName,
            })
            .ToList();
        var sourceIds = values
            .Select(static item => item.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        values.AddRange(resultEvidence
            .Where(item => !sourceIds.Contains(item.SourceId) &&
                !string.IsNullOrWhiteSpace(item.Excerpt))
            .Select(static item => new AnswerQualityEvidence
            {
                SourceId = item.SourceId,
                SourceType = item.SourceType,
                Text = item.Excerpt,
            }));
        return values;
    }
}
