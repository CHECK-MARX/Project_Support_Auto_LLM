using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Core.Cases;

if (args.Length < 5)
{
    Console.Error.WriteLine("Usage: AnswerComparisonLab <candidates.json> <settings.json> <qac-root> <checkmarx-root> <output.json> [--klocwork-root PATH] [--case-ids ID,ID,ID --shadow-status PATH --review-output PATH] [--start N] [--limit N] [--evidence-count N] [--output-tokens N] [--context-tokens N] [--timeout-seconds N] [--compact] [--dry-run]");
    return 2;
}

var candidatePath = Path.GetFullPath(args[0]);
var settingsPath = Path.GetFullPath(args[1]);
var roots = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["QAC"] = Path.GetFullPath(args[2]),
    ["CHECKMARX"] = Path.GetFullPath(args[3]),
};
var klocworkRootIndex = Array.IndexOf(args, "--klocwork-root");
if (klocworkRootIndex >= 0)
{
    if (klocworkRootIndex + 1 >= args.Length)
    {
        throw new ArgumentException("--klocwork-root requires a path.");
    }
    roots["KLOCWORK"] = Path.GetFullPath(args[klocworkRootIndex + 1]);
}
var outputPath = Path.GetFullPath(args[4]);
var caseIdsIndex = Array.IndexOf(args, "--case-ids");
var shadowStatusIndex = Array.IndexOf(args, "--shadow-status");
var reviewOutputIndex = Array.IndexOf(args, "--review-output");
var fixedEvidenceIndex = Array.IndexOf(args, "--fixed-evidence");
var labelsIndex = Array.IndexOf(args, "--shadow-labels");
var shadowPilot = caseIdsIndex >= 0;
var reviewOutputPath = reviewOutputIndex >= 0 && reviewOutputIndex + 1 < args.Length
    ? Path.GetFullPath(args[reviewOutputIndex + 1]) : null;
if (shadowPilot && (caseIdsIndex + 1 >= args.Length || shadowStatusIndex < 0 ||
                    shadowStatusIndex + 1 >= args.Length || args.Contains("--start") || args.Contains("--limit")))
{
    throw new ArgumentException("Shadow Pilot requires --case-ids and --shadow-status without --start/--limit.");
}
if (shadowPilot)
{
    var generatedRoot = Path.GetDirectoryName(candidatePath)!;
    if (Path.GetFileName(generatedRoot) != "generated" || Path.GetFileName(Path.GetDirectoryName(generatedRoot)) != "reports" ||
        !outputPath.StartsWith(generatedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        reviewOutputPath is not null && !reviewOutputPath.StartsWith(generatedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("Shadow Pilot input/output must stay under rag-lab/reports/generated.");
    }
}
if (shadowPilot && (!args.Contains("--dry-run") &&
                    (fixedEvidenceIndex < 0 || fixedEvidenceIndex + 1 >= args.Length) ||
                    labelsIndex < 0 || labelsIndex + 1 >= args.Length))
{
    throw new ArgumentException("Shadow comparison requires --shadow-labels and fixed evidence outside dry-run.");
}
var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
var rankPreview = args.Contains("--rank-preview", StringComparer.Ordinal);
var rankedE2e = args.Contains("--rank-e2e", StringComparer.Ordinal);
var evidenceSelectionMode = rankedE2e ? "ranked_e2e" : rankPreview ? "rank_preview" :
    shadowPilot && dryRun && fixedEvidenceIndex < 0 ? "shadow_evidence_capture" :
    shadowPilot ? "fixed_regression" : "development_default";
if (rankPreview && !dryRun)
{
    throw new ArgumentException("--rank-preview is diagnostic only and requires --dry-run.");
}
if (rankedE2e && (dryRun || rankPreview || !shadowPilot))
{
    throw new ArgumentException("--rank-e2e requires a non-dry-run Shadow Pilot comparison.");
}
var compact = args.Contains("--compact", StringComparer.Ordinal);
var evidenceIndex = Array.IndexOf(args, "--evidence-count");
var evidenceCount = evidenceIndex >= 0 && evidenceIndex + 1 < args.Length && int.TryParse(args[evidenceIndex + 1], out var parsedEvidence)
    ? Math.Clamp(parsedEvidence, 1, 5)
    : 2;
var outputTokenIndex = Array.IndexOf(args, "--output-tokens");
var outputTokens = outputTokenIndex >= 0 && outputTokenIndex + 1 < args.Length && int.TryParse(args[outputTokenIndex + 1], out var parsedTokens)
    ? Math.Clamp(parsedTokens, 100, 800)
    : 600;
var contextIndex = Array.IndexOf(args, "--context-tokens");
var contextTokens = contextIndex >= 0 && contextIndex + 1 < args.Length && int.TryParse(args[contextIndex + 1], out var parsedContext)
    ? Math.Clamp(parsedContext, 1024, 8192)
    : 8192;
var timeoutIndex = Array.IndexOf(args, "--timeout-seconds");
var timeoutSeconds = timeoutIndex >= 0 && timeoutIndex + 1 < args.Length && int.TryParse(args[timeoutIndex + 1], out var parsedTimeout)
    ? Math.Clamp(parsedTimeout, 30, 300)
    : 60;
var startIndex = Array.IndexOf(args, "--start");
var start = startIndex >= 0 && startIndex + 1 < args.Length && int.TryParse(args[startIndex + 1], out var parsedStart)
    ? Math.Clamp(parsedStart, 0, 39)
    : 0;
var limitIndex = Array.IndexOf(args, "--limit");
var limit = limitIndex >= 0 && limitIndex + 1 < args.Length && int.TryParse(args[limitIndex + 1], out var parsedLimit)
    ? Math.Clamp(parsedLimit, 1, 40)
    : 40;
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var candidateSet = JsonSerializer.Deserialize<CandidateSet>(File.ReadAllText(candidatePath), options)
    ?? throw new InvalidDataException("Candidate set is empty.");
if (candidateSet.SchemaVersion != 1 || candidateSet.DataClassification != "private_masked_review_required" ||
    candidateSet.Cases.Count != 60 || candidateSet.DevelopmentCount != 40 || candidateSet.HoldoutCount != 20)
{
    throw new InvalidDataException("Expected a 60-case private review set with a 40/20 split.");
}

var requestedIds = shadowPilot
    ? args[caseIdsIndex + 1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    : [];
if (shadowPilot)
{
    if (requestedIds.Length is not (3 or 5) ||
        requestedIds.Distinct(StringComparer.Ordinal).Count() != requestedIds.Length)
    {
        throw new InvalidDataException("Shadow Pilot requires three or five distinct case IDs.");
    }
    using var document = JsonDocument.Parse(File.ReadAllText(args[shadowStatusIndex + 1]));
    var gate = document.RootElement;
    var candidateSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidatePath))).ToLowerInvariant();
    if (!gate.GetProperty("shadowPilotReady").GetBoolean() || gate.GetProperty("holdoutUsed").GetBoolean() ||
        !string.Equals(gate.GetProperty("candidateSetSha256").GetString(), candidateSha, StringComparison.Ordinal))
    {
        throw new InvalidDataException("The Shadow Pilot gate is not ready for this candidate set.");
    }
    var allowedIds = gate.GetProperty("cases").EnumerateArray()
        .Where(item => item.GetProperty("shadowReady").GetBoolean())
        .Select(item => item.GetProperty("caseId").GetString()).ToHashSet(StringComparer.Ordinal);
    if (requestedIds.Any(id => !allowedIds.Contains(id)))
    {
        throw new InvalidDataException("A requested case is outside the ready Shadow Pilot set.");
    }
}
var byCaseId = candidateSet.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
var fixedEvidence = shadowPilot && fixedEvidenceIndex >= 0
    ? JsonSerializer.Deserialize<FixedEvidenceFile>(File.ReadAllText(args[fixedEvidenceIndex + 1]), options)!
        .Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal)
    : new Dictionary<string, FixedEvidenceCase>(StringComparer.Ordinal);
var labelFile = shadowPilot
    ? JsonSerializer.Deserialize<ShadowLabelFile>(File.ReadAllText(args[labelsIndex + 1]), options)!
    : null;
var labels = labelFile?.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
if (shadowPilot && (labels?.Count != requestedIds.Length ||
                    requestedIds.Any(id => !labels!.ContainsKey(id)) ||
                    !dryRun && (fixedEvidence.Count != requestedIds.Length ||
                                requestedIds.Any(id => !fixedEvidence.ContainsKey(id)))))
{
    throw new InvalidDataException("Fixed evidence or review labels do not match the requested Shadow cases.");
}
var selected = shadowPilot
    ? requestedIds.Select(id => byCaseId.TryGetValue(id, out var item) ? item
        : throw new InvalidDataException($"Unknown case ID: {id}")).ToArray()
    : candidateSet.Cases.Where(item => item.Split == "development").Skip(start).Take(limit).ToArray();
if (selected.Length != (shadowPilot ? requestedIds.Length : limit) ||
    selected.Any(item => item.Split != "development" || !roots.ContainsKey(item.Product)) ||
    shadowPilot && selected.Select(item => item.Product).ToHashSet(StringComparer.Ordinal).Count != 3)
{
    throw new InvalidDataException("The requested development split or product coverage is incomplete.");
}

var casePaths = ResolveCasePaths(roots, candidateSet.Cases.Select(item => item.CandidateId).ToHashSet(StringComparer.Ordinal));
var holdoutCases = candidateSet.Cases.Where(item => item.Split == "holdout")
    .Select(item => casePaths.TryGetValue(item.CandidateId, out var path)
        ? (Path: path, SupportNumber: CaseParser.ParseCaseFromDirectory(new DirectoryInfo(path))?.SupportNumber)
        : throw new InvalidDataException("A holdout case path could not be resolved for exclusion."))
    .ToArray();
var settings = JsonSerializer.Deserialize<AiAssistantSettings>(File.ReadAllText(settingsPath), options)
    ?? throw new InvalidDataException("Settings could not be read.");
var indexFolderIndex = Array.IndexOf(args, "--index-folder");
if (indexFolderIndex >= 0)
{
    if (!shadowPilot || indexFolderIndex + 1 >= args.Length)
    {
        throw new ArgumentException("--index-folder requires a Shadow Pilot index folder.");
    }
    var isolatedIndexFolder = Path.GetFullPath(args[indexFolderIndex + 1]);
    var generatedRoot = Path.GetDirectoryName(candidatePath)!;
    if (!isolatedIndexFolder.StartsWith(generatedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) || !Directory.Exists(isolatedIndexFolder))
    {
        throw new ArgumentException("Shadow Pilot index folder must exist under rag-lab/reports/generated.");
    }
    settings = settings with { AiIndexFolder = isolatedIndexFolder };
}
var provider = settings.LlmProvider with
{
    Provider = "Ollama",
    Endpoint = "http://localhost:11434",
    ChatModel = "qwen3:8b",
    StructuredOutputMode = StructuredOutputModes.Json,
    ThinkingParameterType = ThinkingParameterTypes.Boolean,
    ThinkingValue = "false",
    Temperature = 0,
    MaxOutputTokens = outputTokens,
    ContextWindowTokens = contextTokens,
    TimeoutSeconds = timeoutSeconds,
};
settings = settings with
{
    LlmProvider = provider,
    MaxPromptChars = contextTokens <= 2048 ? 2500 : 8000,
    MaxEvidenceItems = evidenceCount,
    AnswerQualityMode = AnswerQualityModes.Quality,
    DisableThinking = true,
};
var search = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());
var llm = new DiagnosticLlmClient(new OllamaClient());
var safety = new SafetyRedactionService();
var answer = new AiAnswerService(new PromptBuilder(), new EvidenceBuilder(), safety, llm,
    polishingTimeoutOverrideSeconds: timeoutSeconds);
var comparison = new GroundedAnswerComparisonService(llm, safety,
    comparisonTimeoutOverrideSeconds: timeoutSeconds);
var focusExtractor = new InquiryFocusExtractor();
var factResolver = new FactResolver();
var results = new List<CaseResult>();
var reviewResults = new List<object>();

foreach (var item in selected)
{
    if (!casePaths.TryGetValue(item.CandidateId, out var casePath))
    {
        throw new InvalidDataException($"Case {item.CaseId} could not be resolved from the supplied roots.");
    }

    var productName = item.Product switch
    {
        "QAC" => "HelixQAC",
        "CHECKMARX" => "Checkmarx",
        "KLOCWORK" => "Klocwork",
        _ => throw new InvalidDataException($"Unknown product: {item.Product}"),
    };
    var product = settings.Products.FirstOrDefault(value =>
        string.Equals(value.ProductName, productName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"Product settings missing: {productName}");
    var supportNumber = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(casePath))?.SupportNumber;
    var context = new CaseContext { ProductName = productName };
    var focus = focusExtractor.Extract(item.Question, context, settings.UsePhase175QualityControls);
    var officialPreview = rankPreview
        ? await search.SearchOfficialDocumentsAsync(product, settings.AiIndexFolder, focus, 10)
        : [];
    var watch = Stopwatch.StartNew();
    var retrieved = await search.SearchAllHybridAsync(product, settings.AiIndexFolder, focus,
        provider, shadowPilot ? 500 : 50, ragPipelineMode: settings.RagPipelineMode,
        preserveLowRelevanceForRegression: shadowPilot && !rankPreview && !rankedE2e);
    var retrievalSeconds = watch.Elapsed.TotalSeconds;
    var sameCaseHits = retrieved.Count(source => IsSameCase(source, casePath, supportNumber));
    var holdoutHits = retrieved.Count(source => holdoutCases.Any(holdout =>
        IsSameCase(source, holdout.Path, holdout.SupportNumber)));
    var isolated = retrieved.Where(source => !IsSameCase(source, casePath, supportNumber) &&
        !holdoutCases.Any(holdout => IsSameCase(source, holdout.Path, holdout.SupportNumber)));
    var eligible = (compact ? isolated.Where(source => source.Text.Length <= 1200) : isolated).ToArray();
    var retrievalTop10 = eligible.Take(10).Select(source => new
    {
        source.SourceType, source.Title, source.SectionTitle, source.SourceId,
        source.SupportNumber, source.FilePath, source.Text, source.Score,
        source.ScoreBreakdown,
    }).ToArray();
    var extractedQuery = new
    {
        focus.TechnicalQuery.Product, focus.TechnicalQuery.Feature,
        focus.TechnicalQuery.Component, focus.TechnicalQuery.Operation,
        focus.TechnicalQuery.Intent, TechnicalToken = focus.ImportantTerms,
        Version = focus.TargetVersions,
    };
    var currentCaseFacts = shadowPilot
        ? CurrentCaseFactReader.Read(casePath, item.Question, safety)
        : [];
    var sources = shadowPilot && !dryRun && !rankPreview && !rankedE2e
        ? fixedEvidence[item.CaseId].SelectedSources.Select(fixedSource =>
            eligible.FirstOrDefault(source => source.SourceId == fixedSource.SourceId &&
                Sha256(source.Text) == Sha256(fixedSource.Text)) ??
            throw new InvalidDataException($"Fixed evidence changed or is unavailable for {item.CaseId}.")).ToArray()
        : rankedE2e ? SelectNonDuplicateEvidence(eligible, evidenceCount, item.Question) :
            eligible.Take(evidenceCount).ToArray();
    if (sources.Length > evidenceCount || sources.Select(source => source.SourceId).Distinct().Count() != sources.Length)
    {
        throw new InvalidDataException("Fixed evidence count or identity is invalid.");
    }
    if (sources.Any(source => IsSameCase(source, casePath, supportNumber)))
    {
        throw new InvalidDataException("Same-case evidence remained after isolation.");
    }

    if (dryRun || sources.Length == 0 && !shadowPilot)
    {
        results.Add(new CaseResult(item.CaseId, item.Product, item.Topic, sources.Length,
            sameCaseHits, dryRun ? "DryRun" : "NoEvidence", "", "", null, null, null,
            watch.Elapsed.TotalSeconds, retrievalSeconds, null, null)
        {
            SelectedEvidence = sources.Select(DescribeSource).ToArray(),
            ExcludedHoldoutSources = holdoutHits,
        });
        if (reviewOutputPath is not null)
        {
            reviewResults.Add(new { item.CaseId, item.Product, extractedQuery, retrievalTop10,
                officialPreview = officialPreview.Select(source => new { source.SourceId,
                    source.Title, source.Url, source.Score, textLength = source.Text.Length }).ToArray(),
                officialMergeTrace = retrieved.Select((source, index) => new { source, index })
                    .Where(item => item.source.SourceType == "OfficialDoc")
                    .Select(item => new { item.source.SourceId, mergedRank = item.index + 1,
                        item.source.Score, item.source.ScoreBreakdown,
                        textLength = item.source.Text.Length,
                        sameCaseExcluded = IsSameCase(item.source, casePath, supportNumber),
                        compactExcluded = compact && item.source.Text.Length > 1200 })
                    .ToArray(),
                currentCaseFactKeys = currentCaseFacts.Select(fact => fact.Key).ToArray(),
                selectedSources = sources.Select(source => new { source.SourceType, source.Title,
                    source.SectionTitle, source.SourceId, source.SupportNumber,
                    source.FilePath, source.Text }).ToArray() });
            Directory.CreateDirectory(Path.GetDirectoryName(reviewOutputPath)!);
            File.WriteAllText(reviewOutputPath, JsonSerializer.Serialize(new { schemaVersion = 1,
                dataClassification = "private_evidence_text", shadowPilot, cases = reviewResults }, options),
                new UTF8Encoding(false));
        }
        Console.WriteLine($"{item.CaseId}: sources={sources.Length}, excludedSameCase={sameCaseHits}");
        continue;
    }

    var request = new AnswerDraftRequest
    {
        Case = context,
        InquiryText = item.Question,
        InquiryFocus = focus,
        Sources = sources,
        Settings = settings,
    };
    var firstLlmCall = llm.Calls.Count;
    var baseline = await answer.GenerateDraftAsync(request);
    var candidateFirstLlmCall = llm.Calls.Count;
    var baselineCompletedSeconds = watch.Elapsed.TotalSeconds;
    var label = shadowPilot ? labels![item.CaseId] : null;
    var resolved = factResolver.Resolve(productName, string.Empty, item.Question, focus);
    var sourceIds = sources.Select(static source => source.SourceId).ToHashSet(StringComparer.Ordinal);
    var inputFacts = resolved with
    {
        AnswerReadiness = label?.Readiness ?? resolved.AnswerReadiness,
        ResolvedFacts = resolved.ResolvedFacts
            .Where(fact => sourceIds.Contains(fact.EvidenceId) ||
                fact.SourceType == "CurrentInquiry" &&
                item.Question.Contains(fact.Value, StringComparison.OrdinalIgnoreCase))
            .Concat(currentCaseFacts)
            .Concat(SelectedOfficialFactProjector.Project(item.Question, sources))
            .ToArray(),
    };
    var groundedRequest = request with { FactResolution = inputFacts };
    var criteria = label is null ? null : new ShadowReviewCriteria(label.ExpectedClaims,
        label.ForbiddenClaims, label.RequiredEvidence, label.Readiness);
    var compared = await comparison.CompareAsync(groundedRequest, baseline, shadowReview: criteria);
    watch.Stop();
    results.Add(new CaseResult(item.CaseId, item.Product, item.Topic, sources.Length,
        sameCaseHits, compared.Status, baseline.AnswerGenerationMode,
        compared.Reasons.Count == 0 ? "" : string.Join(" | ", compared.Reasons),
        compared.BaselineQuality?.Grounding,
        compared.CandidateQuality?.Grounding,
        compared.CandidateQuality?.UnsupportedClaimCount,
        watch.Elapsed.TotalSeconds, retrievalSeconds,
        baselineCompletedSeconds - retrievalSeconds,
        watch.Elapsed.TotalSeconds - baselineCompletedSeconds,
        llm.Calls.Skip(firstLlmCall).ToArray())
    {
        BaselineReadiness = baseline.Readiness,
        CandidateReadiness = compared.GeneratedReadiness,
        RawGeneratedReadiness = compared.RawGeneratedReadiness,
        BaselineUnsupportedClaims = compared.BaselineQuality?.UnsupportedClaimCount,
        SelectedEvidence = sources.Select(DescribeSource).ToArray(),
        ExcludedHoldoutSources = holdoutHits,
        ExpectedClaimsCoverage = compared.ExpectedClaimsCoverage,
        CitationTraceable = compared.CitationTraces.Count,
        CitationCount = compared.GeneratedCitationCount,
        CitationRelevantCount = compared.CitationRelevantCount,
        AcceptedCitationCount = compared.Candidate?.Evidence.Count ?? 0,
        ExpectedReadiness = label?.Readiness,
        BaselineFallbackReason = ClassifyBaselineFallback(baseline),
    });
    if (reviewOutputPath is not null)
    {
        reviewResults.Add(new { item.CaseId, item.Product, item.Topic, extractedQuery, retrievalTop10,
            baselineReply = baseline.CustomerReplyDraft,
            baselineWarnings = baseline.Warnings,
            baselineModelRaw = candidateFirstLlmCall > firstLlmCall
                ? llm.Contents.ElementAtOrDefault(firstLlmCall) : null,
            groundedModelRaw = llm.Contents.ElementAtOrDefault(candidateFirstLlmCall),
            groundedPrompt = llm.Prompts.ElementAtOrDefault(candidateFirstLlmCall)?.UserPrompt,
            generationInput = new { inputFacts.AnswerReadiness, inputFacts.MissingFacts,
                inputFacts.ResolvedFacts, focus.TechnicalQuery.Component,
                focus.TechnicalQuery.Operation },
            groundedReply = compared.Candidate?.CustomerReplyDraft,
            generatedReply = compared.GeneratedReplyDraft,
            baselineReadiness = baseline.Readiness,
            groundedReadiness = compared.GeneratedReadiness,
            rawGroundedReadiness = compared.RawGeneratedReadiness,
            compared.Status, compared.Reasons,
            compared.CitationTraces, compared.ExpectedClaimsCoverage,
            selectedSources = sources.Select(source => new { source.SourceType, source.Title,
                source.SectionTitle, source.SourceId, source.SupportNumber,
                source.FilePath, source.Text }).ToArray() });
        Directory.CreateDirectory(Path.GetDirectoryName(reviewOutputPath)!);
        File.WriteAllText(reviewOutputPath, JsonSerializer.Serialize(new { schemaVersion = 1,
            dataClassification = "private_answer_text", shadowPilot, cases = reviewResults }, options),
            new UTF8Encoding(false));
    }
    Console.WriteLine($"{item.CaseId}: sources={sources.Length}, excludedSameCase={sameCaseHits}, status={compared.Status}, seconds={watch.Elapsed.TotalSeconds:0.0}");
    SaveReport(outputPath, results, dryRun, evidenceCount, outputTokens, contextTokens, timeoutSeconds,
        compact, shadowPilot, evidenceSelectionMode, options);
}

SaveReport(outputPath, results, dryRun, evidenceCount, outputTokens, contextTokens, timeoutSeconds,
    compact, shadowPilot, evidenceSelectionMode, options);
Console.WriteLine($"Completed {results.Count} development cases. No reply or evidence text was logged to the metrics report.");
return 0;

static Dictionary<string, string> ResolveCasePaths(
    IReadOnlyDictionary<string, string> roots, HashSet<string> wanted)
{
    var found = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var root in roots.Values)
    {
        foreach (var group in Directory.EnumerateDirectories(root))
        {
            foreach (var casePath in Directory.EnumerateDirectories(group))
            {
                var id = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(casePath.ToLowerInvariant())))[..20].ToLowerInvariant();
                if (wanted.Contains(id)) found[id] = casePath;
            }
        }
    }
    return found;
}

static bool IsSameCase(SearchSource source, string casePath, string? supportNumber)
{
    if (!string.IsNullOrWhiteSpace(supportNumber) &&
        string.Equals(source.SupportNumber, supportNumber, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    foreach (var path in new[] { source.FilePath, source.ArchivePath })
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) continue;
        try
        {
            var relative = Path.GetRelativePath(casePath, path);
            if (relative == "." || relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative)) return true;
        }
        catch (ArgumentException) { }
    }
    return false;
}

static void SaveReport(string path, IReadOnlyList<CaseResult> results, bool dryRun,
    int evidenceCount, int outputTokens, int contextTokens, int timeoutSeconds, bool compact, bool shadowPilot,
    string evidenceSelectionMode, JsonSerializerOptions options)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var payload = new { schemaVersion = 1, model = "qwen3:8b", dryRun, compact, shadowPilot,
        evidenceSelectionMode,
        maxEvidenceItems = evidenceCount,
        maxPromptChars = contextTokens <= 2048 ? 2500 : 8000,
        maxOutputTokens = outputTokens, contextWindowTokens = contextTokens,
        timeoutSeconds,
        dataClassification = "private_metrics_only", humanGroundTruthVerified = false,
        sameCaseRetrievalExcluded = true, cases = results };
    var temp = path + ".tmp";
    File.WriteAllText(temp, JsonSerializer.Serialize(payload, options), new UTF8Encoding(false));
    File.Move(temp, path, true);
}

static SelectedEvidenceDiagnostic DescribeSource(SearchSource source) => new(
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.SourceId))).ToLowerInvariant(),
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Text))).ToLowerInvariant(),
    source.SourceType, source.Score, source.ScoreBreakdown);

static string Sha256(string value) => Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static SearchSource[] SelectNonDuplicateEvidence(IEnumerable<SearchSource> ranked, int count,
    string inquiry)
{
    var selected = new List<SearchSource>();
    foreach (var source in ranked.Take(count))
    {
        if (!SelectedOfficialFactProjector.HasDirectTopicOverlap(inquiry, source)) continue;
        var tokens = ContentTokens(source.Text);
        if (selected.Any(existing => existing.SourceType == source.SourceType &&
            NearDuplicate(tokens, ContentTokens(existing.Text)) &&
            DottedVersions(source.Text).SetEquals(DottedVersions(existing.Text))))
            continue;
        selected.Add(source);
    }
    return selected.ToArray();
}

static HashSet<string> ContentTokens(string text) =>
    Regex.Matches(text, @"[一-龠ぁ-んァ-ヴA-Za-z0-9]{3,}")
        .Select(static match => match.Value.ToLowerInvariant())
        .ToHashSet(StringComparer.Ordinal);

static HashSet<string> DottedVersions(string text) =>
    Regex.Matches(text, @"(?<!\d)\d{4}(?:\.\d+)+(?!\d)")
        .Select(static match => match.Value)
        .ToHashSet(StringComparer.Ordinal);

static bool NearDuplicate(HashSet<string> left, HashSet<string> right)
{
    var intersection = left.Count(term => right.Contains(term));
    var union = left.Count + right.Count - intersection;
    return union >= 30 && (double)intersection / union >= 0.78;
}

static string ClassifyBaselineFallback(AnswerDraftResult baseline)
{
    if (baseline.AnswerGenerationMode != AnswerGenerationModes.PolishingFailed)
    {
        return baseline.AnswerGenerationMode;
    }
    if (baseline.Warnings.Any(warning => warning.Contains("保護値検証に失敗", StringComparison.Ordinal)))
    {
        return "validation/protected_values";
    }
    if (baseline.Warnings.Any(warning => warning.Contains("JSON解析に失敗", StringComparison.Ordinal)))
    {
        return "output_schema/json_parse";
    }
    return baseline.Warnings.FirstOrDefault(warning => warning.Contains("Polishing", StringComparison.Ordinal))
        ?? "unknown_polishing_failure";
}

internal sealed record CandidateSet(
    int SchemaVersion, string DataClassification, int DevelopmentCount,
    int HoldoutCount, IReadOnlyList<CandidateCase> Cases);

internal sealed record CandidateCase(
    string CaseId, string CandidateId, string Product, string Topic,
    string Question, string Split);

internal sealed record FixedEvidenceFile(IReadOnlyList<FixedEvidenceCase> Cases);
internal sealed record FixedEvidenceCase(string CaseId, IReadOnlyList<FixedEvidenceSource> SelectedSources);
internal sealed record FixedEvidenceSource(string SourceId, string Text);
internal sealed record ShadowLabelFile(IReadOnlyList<ShadowLabelCase> Cases);
internal sealed record ShadowLabelCase(string CaseId, IReadOnlyList<string> ExpectedClaims,
    IReadOnlyList<string> ForbiddenClaims, IReadOnlyList<string> RequiredEvidence, string Readiness);

internal sealed record CaseResult(
    string CaseId, string Product, string Topic, int Sources,
    int ExcludedSameCaseSources, string Status, string BaselineMode,
    string RejectionReason, double? BaselineGrounding, double? CandidateGrounding,
    int? CandidateUnsupportedClaims, double Seconds, double RetrievalSeconds,
    double? BaselineSeconds, double? CandidateSeconds,
    IReadOnlyList<LlmCallDiagnostic>? LlmCalls = null)
{
    public string? BaselineReadiness { get; init; }
    public string? CandidateReadiness { get; init; }
    public string? RawGeneratedReadiness { get; init; }
    public int? BaselineUnsupportedClaims { get; init; }
    public IReadOnlyList<SelectedEvidenceDiagnostic> SelectedEvidence { get; init; } = [];
    public int ExcludedHoldoutSources { get; init; }
    public string? ExpectedReadiness { get; init; }
    public double? ExpectedClaimsCoverage { get; init; }
    public int CitationTraceable { get; init; }
    public int CitationCount { get; init; }
    public int CitationRelevantCount { get; init; }
    public int AcceptedCitationCount { get; init; }
    public string BaselineFallbackReason { get; init; } = string.Empty;
}

internal sealed record SelectedEvidenceDiagnostic(
    string SourceIdSha256, string TextSha256, string SourceType, double? Score, string ScoreBreakdown);

internal sealed record LlmCallDiagnostic(
    int PromptChars, double Seconds, string Outcome,
    int? PromptTokens, int? OutputTokens, string? DoneReason);

internal sealed class DiagnosticLlmClient(ILlmClient inner) : ILlmClient
{
    private readonly List<LlmCallDiagnostic> calls = [];
    private readonly List<string?> contents = [];
    private readonly List<PromptMessages> prompts = [];
    public IReadOnlyList<LlmCallDiagnostic> Calls => calls;
    public IReadOnlyList<string?> Contents => contents;
    public IReadOnlyList<PromptMessages> Prompts => prompts;

    public async Task<LlmGenerationResult> GenerateAsync(
        PromptMessages messages, LlmProviderSettings settings,
        bool disableThinking = true, CancellationToken cancellationToken = default)
    {
        prompts.Add(messages);
        var watch = Stopwatch.StartNew();
        var promptChars = messages.Diagnostics.FinalPromptChars > 0
            ? messages.Diagnostics.FinalPromptChars
            : messages.SystemPrompt.Length + messages.UserPrompt.Length;
        try
        {
            var result = await inner.GenerateAsync(messages, settings, disableThinking, cancellationToken);
            watch.Stop();
            contents.Add(result.Content);
            calls.Add(new LlmCallDiagnostic(promptChars, watch.Elapsed.TotalSeconds,
                result.ContentReturned ? "ContentReturned" : "EmptyContent",
                result.PromptEvalCount, result.EvalCount, result.DoneReason));
            return result;
        }
        catch (Exception ex)
        {
            watch.Stop();
            contents.Add(null);
            calls.Add(new LlmCallDiagnostic(promptChars, watch.Elapsed.TotalSeconds,
                Classify(ex), null, null, null));
            throw;
        }
    }

    private static string Classify(Exception ex)
    {
        if (ex is OperationCanceledException ||
            ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "TimedOut";
        if (ex.Message.Contains("message.content is empty", StringComparison.OrdinalIgnoreCase)) return "EmptyContent";
        if (ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase)) return "ThinkingOnly";
        if (ex.Message.Contains("returned HTTP", StringComparison.OrdinalIgnoreCase)) return "HttpError";
        if (ex.Message.Contains("response JSON", StringComparison.OrdinalIgnoreCase)) return "ResponseJsonError";
        return ex.GetType().Name;
    }
}
