using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Llm;

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
        var perTypeLimit = Math.Max(maxResults, 1);
        var classification = questionClassifier.Classify(query, inquiryFocus);
        var questionTypes = classification.QuestionTypes;
        var latest = questionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase);
        var includePastCases = !latest;

        var officialTask = SearchOfficialDocumentsAsync(product, aiIndexFolder, inquiryFocus, perTypeLimit, cancellationToken);
        var manualsTask = SearchManualsAsync(product, aiIndexFolder, query, perTypeLimit, cancellationToken);
        var pastCasesTask = includePastCases
            ? SearchPastCasesAsync(product, aiIndexFolder, query, perTypeLimit, cancellationToken)
            : Task.FromResult<IReadOnlyList<SearchSource>>([]);
        var pastAnswersTask = includePastCases
            ? SearchPastAnswersAsync(product, aiIndexFolder, query, perTypeLimit, cancellationToken)
            : Task.FromResult<IReadOnlyList<SearchSource>>([]);
        await Task.WhenAll(officialTask, manualsTask, pastCasesTask, pastAnswersTask);

        var combined = officialTask.Result
            .Concat(manualsTask.Result)
            .Concat(pastAnswersTask.Result)
            .Concat(pastCasesTask.Result)
            .Where(source => string.IsNullOrWhiteSpace(source.ProductName) ||
                string.Equals(source.ProductName, product.ProductName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static source => source.SourceId, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(source => source.Score ?? 0).First())
            .ToList();
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var hybridRanked = providerSettings is not null && !string.IsNullOrWhiteSpace(providerSettings.EmbeddingModel)
            ? await HybridSearchRanker.RankWithEmbeddingsAsync(
                combined,
                query,
                product.ProductName,
                productIndexFolder,
                providerSettings,
                embeddingClient,
                Math.Max(maxResults * 3, maxResults),
                cancellationToken)
            : HybridSearchRanker.Rank(combined, query, product.ProductName, Math.Max(maxResults * 3, maxResults));

        return hybridRanked
            .OrderBy(source => SourcePriority(source.SourceType, questionTypes, inquiryFocus.IsFreshnessSensitive))
            .ThenByDescending(static source => source.Score ?? 0)
            .ThenBy(static source => source.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    private static IReadOnlyList<SearchSource> AttachProductName(
        IReadOnlyList<SearchSource> sources,
        string productName)
    {
        return sources
            .Select(source => source with { ProductName = productName })
            .ToList();
    }

    private static int SourcePriority(
        string? sourceType,
        IReadOnlyList<string> questionTypes,
        bool freshnessSensitive)
    {
        if (freshnessSensitive || questionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return sourceType switch
            {
                "OfficialDoc" => 0,
                "Manual" => 1,
                "ExactPastAnswer" => 3,
                "PastAnswer" => 4,
                "PastCaseNote" => 5,
                _ => 3,
            };
        }

        if (questionTypes.Contains(QuestionTypes.FeatureAvailabilityQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return sourceType switch
            {
                "OfficialDoc" => 0,
                "Manual" => 1,
                "ExactPastAnswer" => 2,
                "PastAnswer" => 3,
                "PastCaseNote" => 4,
                _ => 3,
            };
        }

        if (questionTypes.Contains(QuestionTypes.UpgradePossibilityQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return sourceType switch
            {
                "OfficialDoc" => 0,
                "Manual" => 1,
                "ExactPastAnswer" => 2,
                "PastAnswer" => 3,
                "PastCaseNote" => 4,
                _ => 4,
            };
        }

        var troubleshooting = questionTypes.Contains(QuestionTypes.TroubleshootingQuestion, StringComparer.OrdinalIgnoreCase);
        if (troubleshooting)
        {
            return sourceType switch
            {
                "ExactPastAnswer" => 0,
                "Manual" => 1,
                "OfficialDoc" => 2,
                "PastAnswer" => 3,
                "PastCaseNote" => 4,
                _ => 5,
            };
        }

        return sourceType switch
        {
            "Manual" => 0,
            "OfficialDoc" => 1,
            "ExactPastAnswer" => 2,
            "PastAnswer" => 3,
            "PastCaseNote" => 4,
            _ => 5,
        };
    }
}
