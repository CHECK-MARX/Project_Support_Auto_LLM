using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Tests.Ranking;

public sealed class TopicEntityRankerTests
{
    private static readonly TopicEntityCatalog Catalog = new()
    {
        Components = [new TopicAliasDefinition { CanonicalName = "Validate" }],
        Features =
        [
            new TopicAliasDefinition { CanonicalName = "Stream", Aliases = ["ストリーム"] },
            new TopicAliasDefinition { CanonicalName = "License", Aliases = ["ライセンス"] },
            new TopicAliasDefinition { CanonicalName = "IDE Plugin", Aliases = ["IDEプラグイン"] },
        ],
    };

    [Fact]
    public void Rank_PrefersStreamTopicOverHigherBaseLicenseEvidence()
    {
        var result = Rank(
            Candidate(0, "license", "Validate License configuration and setup", 0.99),
            Candidate(1, "stream", "Validate Stream configuration and setup", 0.40));

        Assert.Equal("stream", result.Selected[0].CandidateId);
        var license = Assert.Single(result.Assessed, item => item.CandidateId == "license");
        Assert.True(license.TopicConflict);
        Assert.Equal(-0.55, license.ConflictPenalty, 2);
    }

    [Fact]
    public void Rank_UsesComplementaryTopicCoverageForTopKSet()
    {
        var result = Rank(
            Candidate(0, "overview", "Validate Stream overview: a Stream is used for organizing analysis data.", 0.70),
            Candidate(1, "duplicate-overview", "Validate Stream overview and purpose.", 0.69),
            Candidate(2, "setup", "Validate Stream setup steps: create and configure the Stream, then associate the QAC project.", 0.62),
            Candidate(3, "verify", "Validate Stream verification: confirm its status after setup.", 0.58));

        Assert.Equal(3, result.Selected.Count);
        Assert.Contains(result.Selected, item => item.CandidateId == "setup");
        Assert.Contains(result.Selected, item => item.CandidateId == "verify");
        Assert.Contains(TopicEntityRanker.OverviewCoverage, result.FinalCoverage);
        Assert.Contains(TopicEntityRanker.SetupCoverage, result.FinalCoverage);
        Assert.Contains(TopicEntityRanker.VerificationCoverage, result.FinalCoverage);
    }

    [Fact]
    public void Rank_ReportsMissingStreamProcedureAndVerificationSeparately()
    {
        var result = Rank(Candidate(0, "overview", "Validate Stream overview and purpose.", 0.8));

        Assert.Contains("MissingSetupProcedure", result.InsufficientReasons);
        Assert.Contains("MissingVerification", result.InsufficientReasons);
        Assert.Contains("LowCoverage", result.InsufficientReasons);
    }

    [Fact]
    public void Rank_ReportsTopicConflictWhenOnlyWrongFeatureExists()
    {
        var result = Rank(Candidate(0, "plugin", "Validate IDE Plugin setup and verification", 1.0));

        Assert.Empty(result.Selected);
        Assert.Contains("NoTopicMatch", result.InsufficientReasons);
        Assert.Contains("TopicConflict", result.InsufficientReasons);
    }

    [Fact]
    public void Rank_KeepsPhase15UploadCoverageForComplementaryChunks()
    {
        const string query = "QACの解析結果をValidateへアップロードするqacli validate buildの方法";
        var uploadCatalog = Catalog with
        {
            Features =
            [
                .. Catalog.Features,
                new TopicAliasDefinition { CanonicalName = "Build upload", Aliases = ["validate build", "build upload"] },
            ],
        };
        var texts = new[]
        {
            "Run qacli validate build --project Demo to upload the analysis result.",
            "Authenticate with a token and associate the Validate project.",
            "After execution, verify the uploaded build in the Validate portal.",
        };
        var candidates = texts.Select((text, index) => new TopicEntityRankingCandidate
        {
            CandidateIndex = index,
            CandidateId = $"upload-{index}",
            Text = text,
            SourceType = "Manual",
            ProductName = "HelixQAC",
            BaseSearchScore = 0.7 - (index * 0.05),
            OriginalRank = index + 1,
            Profile = TopicEntityAnalyzer.Extract(text, uploadCatalog),
        }).ToList();

        var result = TopicEntityRanker.Rank(new TopicEntityRankingRequest
        {
            QueryProfile = TopicEntityAnalyzer.Extract(query, uploadCatalog),
            RequestedProduct = "HelixQAC",
            Candidates = candidates,
            MaxItems = 3,
        });

        Assert.Contains(TopicEntityRanker.UploadCommandCoverage, result.FinalCoverage);
        Assert.Contains(TopicEntityRanker.AuthenticationCoverage, result.FinalCoverage);
        Assert.Contains(TopicEntityRanker.ProjectAssociationCoverage, result.FinalCoverage);
        Assert.Contains(TopicEntityRanker.ValidateVerificationCoverage, result.FinalCoverage);
    }

    private static TopicEntityRankingResult Rank(params TopicEntityRankingCandidate[] candidates)
    {
        const string query = "Validate Streamの機能概要と設定方法を教えてください。";
        return TopicEntityRanker.Rank(new TopicEntityRankingRequest
        {
            QueryProfile = TopicEntityAnalyzer.Extract(query, Catalog),
            RequestedProduct = "HelixQAC",
            Candidates = candidates,
            MaxItems = 3,
        });
    }

    private static TopicEntityRankingCandidate Candidate(int index, string id, string text, double score) => new()
    {
        CandidateIndex = index,
        CandidateId = id,
        Text = text,
        SourceType = "Manual",
        ProductName = "HelixQAC",
        BaseSearchScore = score,
        OriginalRank = index + 1,
        Profile = TopicEntityAnalyzer.Extract(text, Catalog),
    };
}
