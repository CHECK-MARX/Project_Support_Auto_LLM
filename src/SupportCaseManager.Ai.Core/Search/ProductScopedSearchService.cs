using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Search;

public sealed class ProductScopedSearchService : IProductScopedSearchService
{
    private readonly IAiCaseKeywordSearcher caseKeywordSearcher;
    private readonly IAiManualKeywordSearcher manualKeywordSearcher;
    private readonly IAiOfficialDocumentKeywordSearcher officialDocumentKeywordSearcher;
    private readonly IQuestionClassifier questionClassifier;
    private readonly IOllamaEmbeddingClient embeddingClient;
    private readonly ICaseAnswerPairSearcher answerPairSearcher;

    public ProductScopedSearchService(
        IAiCaseKeywordSearcher caseKeywordSearcher,
        IAiManualKeywordSearcher manualKeywordSearcher,
        IAiOfficialDocumentKeywordSearcher? officialDocumentKeywordSearcher = null,
        IQuestionClassifier? questionClassifier = null,
        IOllamaEmbeddingClient? embeddingClient = null,
        ICaseAnswerPairSearcher? answerPairSearcher = null)
    {
        this.caseKeywordSearcher = caseKeywordSearcher ?? throw new ArgumentNullException(nameof(caseKeywordSearcher));
        this.manualKeywordSearcher = manualKeywordSearcher ?? throw new ArgumentNullException(nameof(manualKeywordSearcher));
        this.officialDocumentKeywordSearcher = officialDocumentKeywordSearcher ?? new AiOfficialDocumentKeywordSearcher();
        this.questionClassifier = questionClassifier ?? new QuestionClassifier();
        this.embeddingClient = embeddingClient ?? new OllamaEmbeddingClient();
        this.answerPairSearcher = answerPairSearcher ?? new CaseAnswerPairSearcher();
    }

    public async Task<IReadOnlyList<SearchSource>> SearchPastCasesAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var results = await caseKeywordSearcher.SearchAsync(productIndexFolder, query, maxResults, cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchManualsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var results = await manualKeywordSearcher.SearchAsync(productIndexFolder, query, maxResults, cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchOfficialDocumentsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var results = await officialDocumentKeywordSearcher.SearchAsync(
            product.ProductName,
            aiIndexFolder,
            inquiryFocus,
            maxResults,
            cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchPastAnswersAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var results = await answerPairSearcher.SearchAsync(productIndexFolder, query, maxResults, cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchPastAnswersBySupportNumberAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string supportNumber,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var results = await answerPairSearcher.SearchBySupportNumberAsync(
            productIndexFolder,
            supportNumber,
            maxResults,
            cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchPastAnswersAcrossProductsAsync(
        IReadOnlyList<ProductKnowledgeSettings> products,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        var tasks = products
            .Where(static product => product.IsEnabled && !string.IsNullOrWhiteSpace(product.ProductName))
            .Select(product => SearchPastAnswersAsync(product, aiIndexFolder, query, maxResults, cancellationToken))
            .ToList();
        if (tasks.Count == 0)
        {
            return [];
        }

        await Task.WhenAll(tasks);
        return tasks.SelectMany(static task => task.Result)
            .Where(static source => string.Equals(
                source.SourceType,
                "ExactPastAnswer",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static source => source.Score ?? 0)
            .ThenByDescending(static source => source.RetrievedAt)
            .Take(maxResults)
            .ToList();
    }

    public Task<IReadOnlyList<SearchSource>> SearchAllAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        return SearchAllCoreAsync(
            product,
            aiIndexFolder,
            inquiryFocus,
            providerSettings: null,
            maxResults,
            cancellationToken);
    }

    public Task<IReadOnlyList<SearchSource>> SearchAllHybridAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        LlmProviderSettings providerSettings,
        int maxResults = 8,
        CancellationToken cancellationToken = default,
        string ragPipelineMode = RagPipelineModes.Legacy,
        string? embeddingIndexFolderOverride = null)
    {
        return SearchAllCoreAsync(
            product,
            aiIndexFolder,
            inquiryFocus,
            providerSettings,
            maxResults,
            cancellationToken,
            ragPipelineMode,
            embeddingIndexFolderOverride);
    }

    private async Task<IReadOnlyList<SearchSource>> SearchAllCoreAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        LlmProviderSettings? providerSettings,
        int maxResults,
        CancellationToken cancellationToken,
        string ragPipelineMode = RagPipelineModes.Legacy,
        string? embeddingIndexFolderOverride = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(inquiryFocus);
        var query = string.IsNullOrWhiteSpace(inquiryFocus.TechnicalQuery.CoreQuestion)
            ? inquiryFocus.FocusText
            : inquiryFocus.TechnicalQuery.CoreQuestion;
        var isHybridV2 = string.Equals(ragPipelineMode, RagPipelineModes.HybridV2, StringComparison.OrdinalIgnoreCase);
        var perTypeLimit = isHybridV2
            ? 50
            : Math.Clamp(Math.Max(maxResults, 1) * 3, 1, 120);
        var classification = questionClassifier.Classify(query, inquiryFocus);
        var questionTypes = classification.QuestionTypes;
        var latest = questionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase);
        var includePastCases = !latest;
        var catalog = SupportTopicCatalog.Create(product.ProductName);
        var topicAnalysis = NegationAwareTopicAnalyzer.Analyze(query, catalog);
        var queryVariants = BuildFeatureQueryVariants(query, catalog, topicAnalysis.PrimaryProfile);

        var officialTask = SearchAcrossQueryVariantsAsync(
            queryVariants,
            variant => SearchOfficialDocumentsAsync(
                product,
                aiIndexFolder,
                inquiryFocus with { FocusText = variant },
                perTypeLimit,
                cancellationToken),
            perTypeLimit);
        var manualsTask = SearchAcrossQueryVariantsAsync(
            queryVariants,
            variant => SearchManualsAsync(product, aiIndexFolder, variant, perTypeLimit, cancellationToken),
            perTypeLimit);
        var pastCasesTask = includePastCases
            ? SearchAcrossQueryVariantsAsync(
                queryVariants,
                variant => SearchPastCasesAsync(product, aiIndexFolder, variant, perTypeLimit, cancellationToken),
                perTypeLimit)
            : Task.FromResult<IReadOnlyList<SearchSource>>([]);
        var pastAnswersTask = includePastCases
            ? SearchAcrossQueryVariantsAsync(
                queryVariants,
                variant => SearchPastAnswersAsync(product, aiIndexFolder, variant, perTypeLimit, cancellationToken),
                perTypeLimit)
            : Task.FromResult<IReadOnlyList<SearchSource>>([]);
        await Task.WhenAll(officialTask, manualsTask, pastCasesTask, pastAnswersTask);

        var lexicalCandidates = officialTask.Result
            .Concat(manualsTask.Result)
            .Concat(pastAnswersTask.Result)
            .Concat(pastCasesTask.Result)
            .Where(source => string.IsNullOrWhiteSpace(source.ProductName) ||
                string.Equals(source.ProductName, product.ProductName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static source => $"{source.SourceType}\n{source.SourceId}", StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(source => source.Score ?? 0).First())
            .ToList();
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var embeddingIndexFolder = string.IsNullOrWhiteSpace(embeddingIndexFolderOverride)
            ? productIndexFolder
            : embeddingIndexFolderOverride;
        var vectorCandidates = isHybridV2 && providerSettings is not null &&
            !string.IsNullOrWhiteSpace(providerSettings.EmbeddingModel)
            ? await EmbeddingCandidateSourceLoader.LoadAsync(product.ProductName, productIndexFolder, cancellationToken)
            : [];
        var combined = lexicalCandidates
            .Concat(vectorCandidates)
            .GroupBy(static source => $"{source.SourceType}\n{source.SourceId}", StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(source => source.Score ?? 0).First())
            .ToList();
        var hybridLimit = isHybridV2
            ? Math.Min(Math.Max(100, maxResults), combined.Count)
            : Math.Max(combined.Count, maxResults);
        var hybridRanked = providerSettings is not null && !string.IsNullOrWhiteSpace(providerSettings.EmbeddingModel)
            ? await HybridSearchRanker.RankWithEmbeddingsAsync(
                combined,
                query,
                product.ProductName,
                embeddingIndexFolder,
                providerSettings,
                embeddingClient,
                hybridLimit,
                cancellationToken,
                isHybridV2)
            : HybridSearchRanker.Rank(combined, query, product.ProductName, hybridLimit);

        return RankAndMergeSources(
            hybridRanked,
            topicAnalysis,
            catalog,
            questionTypes,
            inquiryFocus.IsFreshnessSensitive,
            maxResults,
            isHybridV2);
    }

    private static async Task<IReadOnlyList<SearchSource>> SearchAcrossQueryVariantsAsync(
        IReadOnlyList<string> queryVariants,
        Func<string, Task<IReadOnlyList<SearchSource>>> search,
        int maxResults)
    {
        var tasks = queryVariants.Select(search).ToList();
        await Task.WhenAll(tasks);
        return tasks
            .SelectMany(static task => task.Result)
            .GroupBy(static source => $"{source.SourceType}\n{source.SourceId}", StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(source => source.Score ?? 0).First())
            .OrderByDescending(static source => source.Score ?? 0)
            .Take(maxResults)
            .ToList();
    }

    private static IReadOnlyList<string> BuildFeatureQueryVariants(
        string query,
        TopicEntityCatalog catalog,
        TopicEntityProfile queryProfile)
    {
        var variants = new List<string> { query };
        foreach (var feature in catalog.Features.Where(feature => queryProfile.Features.Contains(
                     feature.CanonicalName,
                     StringComparer.OrdinalIgnoreCase)))
        {
            var forms = new[] { feature.CanonicalName }
                .Concat(feature.Aliases)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var replacement in forms)
            {
                var variant = query;
                foreach (var form in forms)
                {
                    variant = ReplaceOrdinalIgnoreCase(variant, form, replacement);
                }

                variants.Add(variant);
            }
        }

        if (queryProfile.Operations.Contains("Analysis", StringComparer.Ordinal))
        {
            variants.Add($"{query} qacli analyze project analysis run analysis");
            variants.Add("qacli analyze project analysis procedure");
        }

        if (queryProfile.Features.Contains("File delivery", StringComparer.OrdinalIgnoreCase))
        {
            variants.Add($"{query} Fiebie ファイル転送 ダウンロード アクセスできない プロキシ SSL ブラウザ");
            variants.Add("Fiebie /api/file/download/content ドメイン許可 代替提供");
        }

        return variants
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string newValue)
    {
        var startIndex = 0;
        while (true)
        {
            var index = value.IndexOf(oldValue, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return value;
            }

            value = string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
            startIndex = index + newValue.Length;
        }
    }

    private static IReadOnlyList<SearchSource> RankAndMergeSources(
        IReadOnlyList<SearchSource> sources,
        NegationAwareTopicAnalysis queryAnalysis,
        TopicEntityCatalog catalog,
        IReadOnlyList<string> questionTypes,
        bool freshnessSensitive,
        int maxResults,
        bool isHybridV2)
    {
        var ranked = sources
            .Select(source => ApplyTopicScore(
                source,
                queryAnalysis,
                catalog,
                questionTypes,
                freshnessSensitive))
            .OrderByDescending(static item => item.Source.Score ?? 0)
            .ThenByDescending(static item => item.Source.RetrievedAt)
            .ThenBy(static item => item.Source.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deduplicated = SuppressExactDuplicates(ranked)
            .Where(item => IsEligibleMergedCandidate(item, queryAnalysis.PrimaryProfile))
            .ToList();
        if (isHybridV2)
        {
            return SelectWithIntentRouting(deduplicated, queryAnalysis.PrimaryProfile, questionTypes, maxResults);
        }

        var selected = new List<RankedSource>();

        foreach (var family in new[] { "OfficialDoc", "Manual", "PastCase" })
        {
            var representative = deduplicated.FirstOrDefault(item =>
                SourceFamily(item.Source.SourceType) == family &&
                IsRelevantRepresentative(item, queryAnalysis.PrimaryProfile));
            if (representative is not null && selected.Count < maxResults)
            {
                selected.Add(representative);
            }
        }

        selected.AddRange(deduplicated
            .Where(candidate => selected.All(existing => !ReferenceEquals(existing, candidate)))
            .Take(Math.Max(0, maxResults - selected.Count)));

        return selected
            .OrderByDescending(static item => item.Source.Score ?? 0)
            .ThenByDescending(static item => item.Source.RetrievedAt)
            .ThenBy(static item => item.Source.Title, StringComparer.OrdinalIgnoreCase)
            .Select(static item => item.Source)
            .ToList();
    }

    private static IReadOnlyList<SearchSource> SelectWithIntentRouting(
        IReadOnlyList<RankedSource> candidates,
        TopicEntityProfile queryProfile,
        IReadOnlyList<string> questionTypes,
        int maxResults)
    {
        return candidates
            .Select(candidate => candidate with
            {
                Source = candidate.Source with
                {
                    Score = ApplyBoundedAdjustment(
                        candidate.Source.Score ?? 0,
                        SourceRoutingAdjustment(candidate.Source.SourceType, queryProfile, questionTypes)),
                },
            })
            .OrderByDescending(static candidate => candidate.Source.Score ?? 0)
            .ThenByDescending(static candidate => candidate.Source.FinalRerankScore ?? candidate.Source.Score ?? 0)
            .ThenBy(static candidate => candidate.Source.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 100))
            .Select(static candidate => candidate.Source)
            .ToList();
    }

    private static double SourceRoutingAdjustment(
        string sourceType,
        TopicEntityProfile queryProfile,
        IReadOnlyList<string> questionTypes)
    {
        var family = SourceFamily(sourceType);
        var isTroubleshooting = questionTypes.Contains(QuestionTypes.TroubleshootingQuestion, StringComparer.OrdinalIgnoreCase);
        var isHowTo = queryProfile.Intents.Contains("HowTo", StringComparer.OrdinalIgnoreCase) ||
            queryProfile.Operations.Count > 0;
        var isSpecification = queryProfile.Features.Contains("Supported Languages", StringComparer.OrdinalIgnoreCase) ||
            queryProfile.Intents.Contains("Overview", StringComparer.OrdinalIgnoreCase);
        if (isTroubleshooting)
        {
            return family == "PastCase" ? 0.12 : family is "OfficialDoc" or "Manual" ? 0.07 : 0;
        }
        if (isSpecification)
        {
            return family == "OfficialDoc" ? 0.14 : family == "Manual" ? 0.08 : family == "PastCase" ? -0.10 : 0;
        }
        if (isHowTo)
        {
            return family == "Manual" ? 0.12 : family == "OfficialDoc" ? 0.07 : family == "PastCase" ? 0.02 : 0;
        }
        return 0;
    }

    private static RankedSource ApplyTopicScore(
        SearchSource source,
        NegationAwareTopicAnalysis queryAnalysis,
        TopicEntityCatalog catalog,
        IReadOnlyList<string> questionTypes,
        bool freshnessSensitive)
    {
        var sourceText = string.Join(
            ' ',
            source.Title,
            source.SectionTitle,
            source.DocumentId,
            source.Text);
        var candidateText = string.Join(
            ' ',
            sourceText,
            string.Join(' ', source.MatchedTerms));
        var candidateProfile = TopicEntityAnalyzer.Extract(candidateText, catalog);
        var assessment = TopicEntityAnalyzer.Compare(queryAnalysis.PrimaryProfile, candidateProfile);
        var adjustment = 0d;
        var reasons = new List<string>();

        if (queryAnalysis.PrimaryProfile.Features.Count > 0)
        {
            if (assessment.MatchedFeatures.Count > 0)
            {
                adjustment += 0.28;
                reasons.Add("feature=match");
            }
            else if (assessment.ConflictKinds.Contains("Feature", StringComparer.Ordinal))
            {
                adjustment -= 0.55;
                reasons.Add("feature=conflict");
            }
            else
            {
                adjustment -= 0.24;
                reasons.Add("feature=missing");
            }

            // Keep incidental mentions from outranking a document whose heading is
            // explicitly about another feature (for example, Stream in a backup guide).
            var headingProfile = TopicEntityAnalyzer.Extract(
                string.Join(' ', source.Title, source.SectionTitle),
                catalog);
            if (headingProfile.Features.Any(feature =>
                    !queryAnalysis.PrimaryProfile.Features.Contains(feature, StringComparer.OrdinalIgnoreCase)))
            {
                adjustment -= 0.55;
                reasons.Add("feature=heading-conflict");
            }

        }

        if (queryAnalysis.PrimaryProfile.Features.Contains("File delivery", StringComparer.OrdinalIgnoreCase))
        {
            var explicitDeliveryMatch = ContainsAny(
                sourceText,
                "Fiebie",
                "Fibe",
                "/api/file/download/content");
            if (explicitDeliveryMatch)
            {
                adjustment += 0.65;
                reasons.Add("feature=file-delivery-explicit-match");
            }
            else
            {
                adjustment -= 0.55;
                reasons.Add("feature=file-delivery-generic-only");
            }
        }


        if (queryAnalysis.PrimaryProfile.Operations.Contains("Analysis", StringComparer.Ordinal))
        {
            if (assessment.MatchedOperations.Contains("Analysis", StringComparer.Ordinal))
            {
                var analysisAdjustment = AnalysisOperationAdjustment(source, candidateText);
                adjustment += analysisAdjustment;
                reasons.Add(analysisAdjustment < 0
                    ? "operation=analysis-secondary-in-unrelated-document"
                    : "operation=analysis-match");
            }
            else
            {
                adjustment -= 0.42;
                reasons.Add("operation=analysis-missing");
            }

            if (!assessment.MatchedOperations.Contains("Analysis", StringComparer.Ordinal) &&
                ContainsUnrelatedAnalysisTopic(candidateText))
            {
                adjustment -= 0.28;
                reasons.Add("operation=unrelated-topic");
            }
        }

        if (queryAnalysis.ExcludedProfile.Features.Count > 0 &&
            NegationAwareTopicAnalyzer.Overlaps(queryAnalysis.ExcludedProfile, candidateProfile))
        {
            adjustment -= 0.55;
            reasons.Add("excludedTopic=match");
        }

        if (freshnessSensitive || questionTypes.Contains(
                QuestionTypes.LatestVersionQuestion,
                StringComparer.OrdinalIgnoreCase))
        {
            adjustment += source.SourceType switch
            {
                "OfficialDoc" => 0.12,
                "Manual" => 0.03,
                "PastCaseNote" or "PastAnswer" or "ExactPastAnswer" => -0.08,
                _ => 0,
            };
        }

        if (questionTypes.Contains(QuestionTypes.TroubleshootingQuestion, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(source.SourceType, "ExactPastAnswer", StringComparison.OrdinalIgnoreCase))
        {
            adjustment += 0.12;
            reasons.Add("troubleshootingExactAnswer=boost");
        }

        var score = ApplyBoundedAdjustment(source.Score ?? 0, adjustment);
        var breakdown = reasons.Count == 0
            ? source.ScoreBreakdown
            : AppendScoreBreakdown(source.ScoreBreakdown, $"topicAdjustment={adjustment:+0.000;-0.000;0.000}, {string.Join(',', reasons)}");
        return new RankedSource(
            source with
            {
                Score = score,
                ScoreBreakdown = breakdown,
            },
            assessment);
    }

    private static IReadOnlyList<RankedSource> SuppressExactDuplicates(IReadOnlyList<RankedSource> ranked)
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<RankedSource>();
        foreach (var item in ranked)
        {
            var fingerprint = TopicEntityAnalyzer.NormalizeText(item.Source.Text);
            if (fingerprint.Length >= 40 && !fingerprints.Add(fingerprint))
            {
                continue;
            }

            results.Add(item);
        }

        return results;
    }

    private static bool IsRelevantRepresentative(RankedSource item, TopicEntityProfile queryProfile)
    {
        if ((item.Source.Score ?? 0) < 0.45 || item.Source.MatchedTerms.Count == 0)
        {
            return false;
        }

        if (queryProfile.Features.Count > 0 && item.Assessment.MatchedFeatures.Count == 0)
        {
            return false;
        }

        if (queryProfile.Features.Contains("File delivery", StringComparer.OrdinalIgnoreCase) &&
            SourceFamily(item.Source.SourceType) == "PastCase" &&
            !ContainsAny(
                string.Join(' ', item.Source.Title, item.Source.SectionTitle, item.Source.DocumentId, item.Source.Text),
                "Fiebie",
                "Fibe",
                "/api/file/download/content"))
        {
            return false;
        }

        return !queryProfile.Operations.Contains("Analysis", StringComparer.Ordinal) ||
            item.Assessment.MatchedOperations.Contains("Analysis", StringComparer.Ordinal);
    }

    private static bool IsEligibleMergedCandidate(RankedSource item, TopicEntityProfile queryProfile)
    {
        if (!queryProfile.Features.Contains("File delivery", StringComparer.OrdinalIgnoreCase) ||
            SourceFamily(item.Source.SourceType) != "PastCase")
        {
            return true;
        }

        return ContainsAny(
            string.Join(' ', item.Source.Title, item.Source.SectionTitle, item.Source.DocumentId, item.Source.Text),
            "Fiebie",
            "Fibe",
            "/api/file/download/content");
    }

    private static bool ContainsUnrelatedAnalysisTopic(string value) =>
        value.Contains("Dashboard", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("IDE", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Eclipse", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Backup", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("バックアップ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("ライセンスサーバ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("license server", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Installation Notes", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("インストール", StringComparison.OrdinalIgnoreCase);

    private static double AnalysisOperationAdjustment(SearchSource source, string candidateText)
    {
        var heading = string.Join(' ', source.Title, source.SectionTitle);
        var operationInHeading = ContainsAny(
            heading,
            "qacli analyze", "analyze project", "project analysis",
            "プロジェクトを解析", "プロジェクトの解析", "解析を実行");
        var unrelatedHeading = ContainsAny(
            heading,
            "Dashboard", "ダッシュボード",
            "License", "ライセンス",
            "IDE", "Visual Studio", "Eclipse",
            "Backup", "バックアップ",
            "Installation", "インストール",
            "qacli validate build", "qacli validate cibuild", "upload", "アップロード");

        if (unrelatedHeading && !operationInHeading)
        {
            return -0.38;
        }

        if (operationInHeading)
        {
            return 0.28;
        }

        if (ContainsAnalysisGuiProcedure(candidateText))
        {
            return 0.55;
        }

        if (ContainsValidateWorkflow(candidateText))
        {
            return -0.48;
        }

        if (ContainsAny(candidateText, "qacli analyze", "qaclianalyze"))
        {
            return 0.30;
        }

        return ContainsAny(candidateText, "プロジェクトを解析", "analyze project")
            ? 0.16
            : 0.04;
    }

    private static bool ContainsValidateWorkflow(string value) =>
        ContainsAny(value, "qacli validate", "validate build", "validate cibuild") ||
        (value.Contains("Validate", StringComparison.OrdinalIgnoreCase) &&
         ContainsAny(value, "upload", "アップロード"));

    private static bool ContainsAnalysisGuiProcedure(string value) =>
        ContainsAny(
            value,
            "]>[解析]>", "]>[解析(", "］＞［解析］＞", "［解析（",
            "プロジェクト全体のファイルベース解析", "Analyze Project", "Run Analysis") ||
        (ContainsAny(value, "QAGUIで", "QA GUIで", "GUIで") &&
         ContainsAny(value, "解析を実行", "解析を開始", "ファイルベース解析を実行"));

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static double ApplyBoundedAdjustment(double score, double adjustment)
    {
        var normalized = Math.Clamp(score, 0, 1);
        return adjustment >= 0
            ? normalized + ((1 - normalized) * Math.Clamp(adjustment, 0, 0.95))
            : normalized * (1 + Math.Clamp(adjustment, -1, 0));
    }

    private static string SourceFamily(string? sourceType) => sourceType switch
    {
        "OfficialDoc" => "OfficialDoc",
        "Manual" => "Manual",
        "PastCaseNote" or "PastAnswer" or "ExactPastAnswer" => "PastCase",
        _ => sourceType ?? string.Empty,
    };

    private static string AppendScoreBreakdown(string? existing, string value) =>
        string.IsNullOrWhiteSpace(existing) ? value : $"{existing}; {value}";

    private sealed record RankedSource(SearchSource Source, TopicConflictAssessment Assessment);

    private static IReadOnlyList<SearchSource> AttachProductName(
        IReadOnlyList<SearchSource> sources,
        string productName)
    {
        return sources
            .Select(source => source with { ProductName = productName })
            .ToList();
    }

}
