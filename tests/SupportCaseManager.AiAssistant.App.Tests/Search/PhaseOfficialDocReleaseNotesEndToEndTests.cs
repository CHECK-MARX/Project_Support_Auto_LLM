using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests.Search;

public sealed class PhaseOfficialDocReleaseNotesEndToEndTests
{
    [Fact]
    public async Task ReleaseNotesQuery_ReachesOfficialSelectionAndSendPlan()
    {
        using var temp = new TempDirectory();
        var product = new ProductKnowledgeSettings
        {
            ProductName = "Checkmarx",
            DocumentUrls = ["https://docs.checkmarx.com/"],
        };
        await WriteOfficialIndexAsync(temp.Path, [
            new AiIndexedOfficialDocument
            {
                Id = "official-976",
                ProductName = "Checkmarx",
                Title = "Engine Pack Version 9.7.6",
                SectionTitle = "Release Notes",
                Url = "https://docs.checkmarx.com/en/engine-pack-version-9-7-6.html",
                Text = "CxSAST Release Notes for Engine Pack Version 9.7.6.",
                RetrievedAt = DateTimeOffset.UtcNow,
                ContentHash = "official-976",
            },
        ]);

        var focus = new InquiryFocusExtractor().Extract(
            "CxSAST9.7.6のリリースノートについて、内容をご教授ください。");
        var allSources = await new ProductScopedSearchService(
                new AiCaseKeywordSearcher(),
                new AiManualKeywordSearcher(),
                new AiOfficialDocumentKeywordSearcher())
            .SearchAllAsync(product, temp.Path, focus, maxResults: 36);

        Assert.Contains(allSources, source => source.SourceType == "OfficialDoc");
        Assert.Contains(allSources, source => source.ScoreBreakdown.Contains(
            "RetrievalMode=OfficialDocDirect",
            StringComparison.Ordinal));
        Assert.DoesNotContain(allSources, source => source.SourceType is "PastCaseNote" or "PastAnswer" or "ExactPastAnswer");

        var autoSelected = allSources
            .Select((source, index) => (source, index))
            .Where(item => FreshnessEvidenceAutoSelector.ShouldAutoSelect(item.source, focus.IsFreshnessSensitive, 0.30))
            .OrderByDescending(item => item.source.Score ?? 0)
            .ThenBy(item => item.index)
            .Take(2)
            .Select(item => item.index)
            .ToHashSet();
        var viewModels = allSources
            .Select((source, index) => new SearchSourceViewModel(source, autoSelected.Contains(index)))
            .ToList();

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            viewModels,
            SearchSourceFiltering.All,
            maxEvidenceItems: 2,
            autoSelectMinimumScore: 0.30,
            minimumDisplayScore: 0,
            isFreshnessSensitive: focus.IsFreshnessSensitive,
            enableTopNFallback: true);

        Assert.True(summary.Selection.OfficialDocSelectedCount > 0);
        Assert.True(summary.Selection.OfficialDocSendCount > 0);
        Assert.Equal(0, summary.Selection.PastCaseNoteSendCount);
    }

    private static async Task WriteOfficialIndexAsync(
        string aiIndexFolder,
        IReadOnlyList<AiIndexedOfficialDocument> documents)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, "Checkmarx");
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(System.IO.Path.Combine(
            productFolder,
            AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = "Checkmarx",
            Documents = documents,
        });
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SupportCaseManager-OfficialDoc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort cleanup for test-only temporary data.
            }
        }
    }
}
