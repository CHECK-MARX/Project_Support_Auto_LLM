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
            [CreateSource("source-1", 0.9), CreateSource("source-2", 0.8), CreateSource("source-3", 0.7)],
            maxEvidenceItems: 2);

        var evidence = builder.BuildEvidence(request);

        Assert.Equal(2, evidence.Count);
        Assert.DoesNotContain(evidence, item => item.SourceId == "source-3");
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
