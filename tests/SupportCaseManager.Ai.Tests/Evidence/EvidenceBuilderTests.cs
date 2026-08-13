using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public class EvidenceBuilderTests
{
    [Fact]
    public void BuildEvidence_CreatesEvidenceItemsFromSearchSources()
    {
        var builder = new EvidenceBuilder();
        var request = CreateRequest([CreateSource("source-1", 0.8)]);

        var evidence = builder.BuildEvidence(request);

        Assert.Single(evidence);
        Assert.Equal("source-1", evidence[0].SourceId);
        Assert.Equal("PastCase", evidence[0].SourceType);
        Assert.Equal("類似案件", evidence[0].Title);
        Assert.Equal("過去案件の本文", evidence[0].Excerpt);
    }

    [Fact]
    public void BuildEvidence_RespectsMaxEvidenceItems()
    {
        var builder = new EvidenceBuilder();
        var request = CreateRequest(
            [
                CreateSource("source-1", 0.9),
                CreateSource("source-2", 0.8) with { Title = "Manual B", Text = "Distinct manual content B." },
                CreateSource("source-3", 0.7) with { Title = "Official C", Text = "Distinct official content C." },
            ],
            maxEvidenceItems: 2);

        var evidence = builder.BuildEvidence(request);

        Assert.Equal(2, evidence.Count);
        Assert.DoesNotContain(evidence, item => item.SourceId == "source-3");
    }

    [Fact]
    public void BuildEvidence_CoverageAwareModePreservesPreselectedSet()
    {
        var sources = Enumerable.Range(1, 4)
            .Select(index => CreateSource($"source-{index}", 0.9 - (index * 0.1)) with
            {
                Title = $"Title {index}",
                Text = $"Distinct evidence {index}",
            })
            .ToList();
        var request = CreateRequest(sources, maxEvidenceItems: 3) with
        {
            Settings = new AiAssistantSettings
            {
                MaxEvidenceItems = 3,
                UseCoverageAwareEvidenceSelection = true,
            },
        };

        var evidence = new EvidenceBuilder().BuildEvidence(request);

        Assert.Equal(4, evidence.Count);
        Assert.Equal(sources.Select(static source => source.SourceId), evidence.Select(static item => item.SourceId));
    }

    [Fact]
    public void BuildEvidence_RemovesDuplicateSourcesBeforeApplyingLimit()
    {
        var builder = new EvidenceBuilder();
        var duplicate = CreateSource("source-2", 0.8) with { Url = "https://example.test/doc" };
        var request = CreateRequest(
            [
                duplicate with { SourceId = "source-1", Score = 0.9 },
                duplicate,
                CreateSource("source-3", 0.7) with { Title = "Manual B", Text = "Distinct manual content B." },
            ],
            maxEvidenceItems: 3);

        var evidence = builder.BuildEvidence(request);

        Assert.Equal(2, evidence.Count);
        Assert.Contains(evidence, item => item.SourceId == "source-1");
        Assert.DoesNotContain(evidence, item => item.SourceId == "source-2");
    }

    [Fact]
    public void BuildEvidence_KeepsDistinctSectionsFromSameUrl()
    {
        var builder = new EvidenceBuilder();
        var request = CreateRequest(
            [
                CreateSource("source-1", 0.9) with { Title = "Manual", SectionTitle = "Overview", Text = "alpha overview content", Url = "https://example.test/doc/" },
                CreateSource("source-2", 0.8) with { Title = "Manual", SectionTitle = "Setup", Text = "beta setup content", Url = "https://example.test/doc" },
            ]);

        var evidence = builder.BuildEvidence(request);

        Assert.Equal(["source-1", "source-2"], evidence.Select(static item => item.SourceId));
    }

    [Fact]
    public void BuildEvidence_MapsScoreToRelevance()
    {
        var builder = new EvidenceBuilder();
        var request = CreateRequest([CreateSource("source-1", 0.83)]);

        var evidence = builder.BuildEvidence(request);

        Assert.Equal(0.83, evidence[0].Relevance);
    }

    [Fact]
    public void BuildEvidence_PreservesDocumentLocationMetadata()
    {
        var source = CreateSource("source-1", 0.83) with
        {
            DocumentTitle = "Perforce-QAC-Manual",
            PageNumber = 14,
            SectionTitle = "Analyzing a project",
            ChunkId = "chunk-14",
            DocumentId = "manual-1",
            ArchivePath = @"C:\docs\manual.zip",
            EntryPath = "manual/Perforce-QAC-Manual.pdf",
            Url = "https://example.test/manual#analysis",
        };

        var item = Assert.Single(new EvidenceBuilder().BuildEvidence(CreateRequest([source])));

        Assert.Equal(source.DocumentTitle, item.DocumentTitle);
        Assert.Equal(source.PageNumber, item.PageNumber);
        Assert.Equal(source.SectionTitle, item.SectionTitle);
        Assert.Equal(source.ChunkId, item.ChunkId);
        Assert.Equal(source.DocumentId, item.DocumentId);
        Assert.Equal(source.ArchivePath, item.ArchivePath);
        Assert.Equal(source.EntryPath, item.EntryPath);
        Assert.Equal(source.Url, item.Url);
    }

    [Fact]
    public void CalculateConfidence_ReturnsZeroWhenNoEvidence()
    {
        var builder = new EvidenceBuilder();
        var request = CreateRequest([]);

        var confidence = builder.CalculateConfidence(request, []);

        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public void CalculateConfidence_IncreasesWithMultipleHighScoreEvidenceItems()
    {
        var builder = new EvidenceBuilder();
        var singleRequest = CreateRequest([CreateSource("source-1", 0.8)]);
        var multipleRequest = CreateRequest([CreateSource("source-1", 0.8), CreateSource("source-2", 0.9)]);

        var singleConfidence = builder.CalculateConfidence(singleRequest, builder.BuildEvidence(singleRequest));
        var multipleConfidence = builder.CalculateConfidence(multipleRequest, builder.BuildEvidence(multipleRequest));

        Assert.True(multipleConfidence > singleConfidence);
    }

    private static AnswerDraftRequest CreateRequest(IReadOnlyList<SearchSource> sources, int maxEvidenceItems = 8)
    {
        return new AnswerDraftRequest
        {
            Sources = sources,
            Settings = new AiAssistantSettings
            {
                MaxEvidenceItems = maxEvidenceItems,
            },
        };
    }

    private static SearchSource CreateSource(string sourceId, double score)
    {
        return new SearchSource
        {
            SourceId = sourceId,
            SourceType = "PastCase",
            Title = "類似案件",
            Text = "過去案件の本文",
            Score = score,
        };
    }
}
