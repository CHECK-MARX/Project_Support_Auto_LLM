using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Ranking;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class PhaseOfficialDocReleaseNotesTests
{
    private const string ReleaseNotesQuestion = "CxSAST9.7.6のリリースノートについて、内容をご教授ください。";

    [Fact]
    public void ReleaseNotesQuestion_ExtractsProductVersionIntentAndSignal()
    {
        var focus = new InquiryFocusExtractor().Extract(ReleaseNotesQuestion);
        var profile = TopicEntityAnalyzer.Extract(
            focus.FocusText,
            SupportTopicCatalog.Create("Checkmarx"));

        Assert.Contains("9.7.6", focus.TargetVersions);
        Assert.True(focus.IsFreshnessSensitive);
        Assert.Contains("リリースノート", focus.ImportantTerms);
        Assert.Contains("ReleaseNotes", focus.TechnicalQuery.Intent);
        Assert.Contains("Checkmarx", profile.Products);
        Assert.Contains("Release Notes", profile.Features);
    }

    [Fact]
    public async Task SearchAsync_MatchesDottedVersionInVersionedUrlAndHeading()
    {
        using var temp = new TempDirectory();
        await WriteOfficialIndexAsync(temp.Path, [
            CreateDocument(
                "official-976",
                "Engine Pack Version 9.7.6",
                "https://docs.checkmarx.com/en/34965-591177-engine-pack-version-9-7-6.html",
                "Engine Pack Version 9.7.6. CxSAST Release Notes and resolved issues."),
            CreateDocument(
                "official-977",
                "Engine Pack Version 9.7.7",
                "https://docs.checkmarx.com/en/engine-pack-version-9-7-7.html",
                "Engine Pack Version 9.7.7. CxSAST Release Notes."),
        ]);

        var focus = new InquiryFocusExtractor().Extract(ReleaseNotesQuestion);
        var results = await new AiOfficialDocumentKeywordSearcher().SearchAsync(
            "Checkmarx",
            temp.Path,
            focus,
            maxResults: 8);

        var result = Assert.Single(results);
        Assert.Equal("official-976", result.SourceId);
        Assert.Contains("Engine Pack Version 9.7.6", result.Title);
        Assert.Contains("9.7.6", result.MatchedTerms);
        Assert.Contains("exactVersionHeading=true", result.ScoreBreakdown);
        Assert.True(result.Score >= 0.65);
    }

    [Fact]
    public async Task SearchAsync_NonexistentVersionDoesNotReturnAnotherRelease()
    {
        using var temp = new TempDirectory();
        await WriteOfficialIndexAsync(temp.Path, [
            CreateDocument(
                "official-976",
                "Engine Pack Version 9.7.6",
                "https://docs.checkmarx.com/en/engine-pack-version-9-7-6.html",
                "Engine Pack Version 9.7.6. CxSAST Release Notes."),
        ]);

        var focus = new InquiryFocusExtractor().Extract(
            "CxSAST9.9.99のリリースノートについて、内容をご教授ください。");
        var results = await new AiOfficialDocumentKeywordSearcher().SearchAsync(
            "Checkmarx",
            temp.Path,
            focus,
            maxResults: 8);

        Assert.Empty(results);
    }

    [Fact]
    public async Task DirectResolver_ResolvesExactOfficialPageWithoutGenericScore()
    {
        using var temp = new TempDirectory();
        const string url976 = "https://docs.checkmarx.com/en/34965-591177-engine-pack-version-9-7-6.html";
        await WriteOfficialIndexAsync(temp.Path, [
            CreateDocument(
                "official-976-01",
                "Engine Pack Version 9.7.6",
                url976,
                "C++20 support was added in Engine Pack Version 9.7.6."),
            CreateDocument(
                "official-976-02",
                "Engine Pack Version 9.7.6",
                url976,
                "Resolved issues for Engine Pack Version 9.7.6."),
            CreateDocument(
                "official-977",
                "Engine Pack Version 9.7.7",
                "https://docs.checkmarx.com/en/engine-pack-version-9-7-7.html",
                "Engine Pack Version 9.7.7 release notes."),
        ]);

        var focus = new InquiryFocusExtractor().Extract(ReleaseNotesQuestion);
        var results = await new OfficialDocDirectResolver().ResolveAsync(
            "Checkmarx",
            temp.Path,
            focus,
            maxResults: 8);

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal("OfficialDoc", result.SourceType);
            Assert.Equal(1.0, result.Score);
            Assert.Contains("RetrievalMode=OfficialDocDirect", result.ScoreBreakdown, StringComparison.Ordinal);
            Assert.Equal("MANDATORY_OFFICIAL_EVIDENCE", result.EvidenceKind);
            Assert.Equal(url976, result.Url);
        });
        Assert.DoesNotContain(results, result => result.Title.Contains("9.7.7", StringComparison.Ordinal));

        var focus977 = new InquiryFocusExtractor().Extract("Engine Pack 9.7.7の変更点を教えてください。");
        var results977 = await new OfficialDocDirectResolver().ResolveAsync(
            "Checkmarx",
            temp.Path,
            focus977,
            maxResults: 8);

        var result977 = Assert.Single(results977);
        Assert.Contains("9.7.7", result977.Title, StringComparison.Ordinal);
        Assert.Equal("https://docs.checkmarx.com/en/engine-pack-version-9-7-7.html", result977.Url);
        Assert.DoesNotContain(results977, result => result.Title.Contains("9.7.6", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectResolver_DoesNotResolveNonReleaseOrUnknownVersion()
    {
        using var temp = new TempDirectory();
        await WriteOfficialIndexAsync(temp.Path, [
            CreateDocument(
                "official-976",
                "Engine Pack Version 9.7.6",
                "https://docs.checkmarx.com/en/engine-pack-version-9-7-6.html",
                "Engine Pack Version 9.7.6 release notes."),
        ]);
        var resolver = new OfficialDocDirectResolver();

        var howTo = new InquiryFocusExtractor().Extract("CxSASTの解析手順を教えてください。");
        var unknownVersion = new InquiryFocusExtractor().Extract(
            "CxSAST9.9.99のリリースノートについて、内容を教えてください。");

        Assert.Empty(await resolver.ResolveAsync("Checkmarx", temp.Path, howTo, 8));
        Assert.Empty(await resolver.ResolveAsync("Checkmarx", temp.Path, unknownVersion, 8));
    }

    [Fact]
    public async Task SearchAllAsync_ExactReleaseNotesBypassesPastCaseAndManualSearch()
    {
        using var temp = new TempDirectory();
        const string url976 = "https://docs.checkmarx.com/en/34965-591177-engine-pack-version-9-7-6.html";
        await WriteOfficialIndexAsync(temp.Path, [
            CreateDocument(
                "official-976",
                "Engine Pack Version 9.7.6",
                url976,
                "C++20 support and resolved issues for Engine Pack Version 9.7.6."),
        ]);

        var focus = new InquiryFocusExtractor().Extract(ReleaseNotesQuestion);
        var results = await new ProductScopedSearchService(
                new ThrowingCaseSearcher(),
                new ThrowingManualSearcher(),
                new AiOfficialDocumentKeywordSearcher())
            .SearchAllAsync(
                new ProductKnowledgeSettings { ProductName = "Checkmarx" },
                temp.Path,
                focus,
                maxResults: 8);

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal("OfficialDoc", result.SourceType));
        Assert.Contains(results, result => result.ScoreBreakdown.Contains(
            "RetrievalMode=OfficialDocDirect",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAllAsync_ReleaseNotesDoesNotUsePastCaseFallback()
    {
        var official = new SearchSource
        {
            SourceId = "official-976",
            SourceType = "OfficialDoc",
            ProductName = "Checkmarx",
            Title = "Engine Pack Version 9.7.6",
            SectionTitle = "Release Notes",
            Text = "CxSAST Release Notes for Engine Pack Version 9.7.6.",
            Url = "https://docs.checkmarx.com/en/engine-pack-version-9-7-6.html",
            Score = 0.85,
            MatchedTerms = ["9.7.6", "Release Notes"],
        };
        var pastCase = new SearchSource
        {
            SourceId = "past-customer-case",
            SourceType = "PastCaseNote",
            ProductName = "Checkmarx",
            Title = "Past customer release note question",
            Text = "Customer-specific text must not be used as release evidence.",
            Score = 0.99,
        };
        var service = new ProductScopedSearchService(
            new FixedCaseSearcher(pastCase),
            new EmptyManualSearcher(),
            new FixedOfficialSearcher(official));

        using var temp = new TempDirectory();
        var focus = new InquiryFocusExtractor().Extract(ReleaseNotesQuestion);
        var results = await service.SearchAllAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Checkmarx",
                DocumentUrls = ["https://docs.checkmarx.com/"],
            },
            temp.Path,
            focus,
            maxResults: 8);

        Assert.Contains(results, source => source.SourceType == "OfficialDoc");
        Assert.DoesNotContain(results, source => source.SourceType == "PastCaseNote");
    }

    private static AiIndexedOfficialDocument CreateDocument(
        string id,
        string title,
        string url,
        string text) =>
        new()
        {
            Id = id,
            ProductName = "Checkmarx",
            Title = title,
            SectionTitle = "Release Notes",
            Url = url,
            Text = text,
            RetrievedAt = DateTimeOffset.UtcNow,
            ContentHash = id,
        };

    private static async Task WriteOfficialIndexAsync(
        string aiIndexFolder,
        IReadOnlyList<AiIndexedOfficialDocument> documents)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, "Checkmarx");
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(
            productFolder,
            AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = "Checkmarx",
            Documents = documents,
        });
    }

    private sealed class FixedOfficialSearcher : IAiOfficialDocumentKeywordSearcher
    {
        private readonly IReadOnlyList<SearchSource> results;

        public FixedOfficialSearcher(params SearchSource[] results)
        {
            this.results = results;
        }

        public Task<IReadOnlyList<SearchSource>> SearchAsync(
            string productName,
            string indexFolder,
            InquiryFocus inquiryFocus,
            int maxResults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(results);
    }

    private sealed class FixedCaseSearcher : IAiCaseKeywordSearcher
    {
        private readonly IReadOnlyList<SearchSource> results;

        public FixedCaseSearcher(params SearchSource[] results)
        {
            this.results = results;
        }

        public Task<IReadOnlyList<SearchSource>> SearchAsync(
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(results);
    }

    private sealed class EmptyManualSearcher : IAiManualKeywordSearcher
    {
        public Task<IReadOnlyList<SearchSource>> SearchAsync(
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchSource>>([]);
    }

    private sealed class ThrowingCaseSearcher : IAiCaseKeywordSearcher
    {
        public Task<IReadOnlyList<SearchSource>> SearchAsync(
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PastCase search must not run after direct official resolution.");
    }

    private sealed class ThrowingManualSearcher : IAiManualKeywordSearcher
    {
        public Task<IReadOnlyList<SearchSource>> SearchAsync(
            string aiIndexFolder,
            string query,
            int maxResults = 8,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Manual search must not run after direct official resolution.");
    }
}
