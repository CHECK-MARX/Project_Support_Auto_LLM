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
        CancellationToken cancellationToken = default)
    {
        return SearchAllCoreAsync(
            product,
            aiIndexFolder,
            inquiryFocus,
            providerSettings,
            maxResults,
            cancellationToken);
    }

    private async Task<IReadOnlyList<SearchSource>> SearchAllCoreAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        LlmProviderSettings? providerSettings,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(inquiryFocus);
        var query = inquiryFocus.FocusText;
        var perTypeLimit = Math.Clamp(Math.Max(maxResults, 1) * 3, 1, 120);
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

        var combined = officialTask.Result
            .Concat(manualsTask.Result)
            .Concat(pastAnswersTask.Result)
            .Concat(pastCasesTask.Result)
            .Where(source => string.IsNullOrWhiteSpace(source.ProductName) ||
                string.Equals(source.ProductName, product.ProductName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static source => $"{source.SourceType}\n{source.SourceId}", StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(source => source.Score ?? 0).First())
            .ToList();
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var hybridLimit = Math.Max(combined.Count, maxResults);
        var hybridRanked = providerSettings is not null && !string.IsNullOrWhiteSpace(providerSettings.EmbeddingModel)
            ? await HybridSearchRanker.RankWithEmbeddingsAsync(
                combined,
                query,
                product.ProductName,
                productIndexFolder,
                providerSettings,
                embeddingClient,
                hybridLimit,
                cancellationToken)
            : HybridSearchRanker.Rank(combined, query, product.ProductName, hybridLimit);

        return RankAndMergeSources(
            hybridRanked,
            topicAnalysis,
            catalog,
            questionTypes,
            inquiryFocus.IsFreshnessSensitive,
            maxResults);
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
        int maxResults)
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
        var deduplicated = SuppressExactDuplicates(ranked);
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

    private static RankedSource ApplyTopicScore(
        SearchSource source,
        NegationAwareTopicAnalysis queryAnalysis,
        TopicEntityCatalog catalog,
        IReadOnlyList<string> questionTypes,
        bool freshnessSensitive)
    {
        var candidateText = string.Join(
            ' ',
            source.Title,
            source.SectionTitle,
            source.DocumentId,
            source.Text,
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

        var score = Math.Clamp((source.Score ?? 0) + adjustment, 0, 1);
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

        return queryProfile.Features.Count == 0 || item.Assessment.MatchedFeatures.Count > 0;
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
