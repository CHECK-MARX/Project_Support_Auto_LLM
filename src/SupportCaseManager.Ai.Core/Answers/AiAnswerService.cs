using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Core.Answers;

public sealed class AiAnswerService : IAiAnswerService
{
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
        var promptMessages = promptBuilder.Build(request);
        var generation = await llmClient.GenerateAsync(
            promptMessages,
            request.Settings.LlmProvider,
            request.Settings.DisableThinking,
            cancellationToken);

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

        var postProcessed = AnswerPostProcessor.Process(
            request,
            processed,
            resultEvidence,
            confidence,
            processed.Warnings);

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
