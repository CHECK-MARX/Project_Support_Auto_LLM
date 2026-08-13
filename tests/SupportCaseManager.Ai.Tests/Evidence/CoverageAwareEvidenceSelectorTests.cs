using System.Text.Json;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class CoverageAwareEvidenceSelectorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Select_DoesNotTreatDifferentDocumentsAsDuplicatesForOneGenericTechnicalToken()
    {
        var request = new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = ["AnalysisProcedure", "AnalysisCommand", "AnalysisVerification"],
            BaseMaxItems = 3,
            ExpansionMaxItems = 3,
            CharacterBudget = 5000,
            MinimumQualityScore = 0,
            Candidates =
            [
                Candidate("official", 1, ["AnalysisProcedure", "AnalysisCommand", "AnalysisVerification"], 0.9) with
                {
                    SourceType = "OfficialDoc",
                    DocumentId = "official-doc",
                    Text = "Official qacli analyze procedure",
                    TechnicalTokens = ["QAC"],
                    EstimatedChars = 500,
                },
                Candidate("manual", 2, ["AnalysisProcedure", "AnalysisCommand"], 0.8) with
                {
                    SourceType = "Manual",
                    DocumentId = "manual-doc",
                    Text = "Manual qacli analyze procedure",
                    TechnicalTokens = ["QAC"],
                    EstimatedChars = 500,
                },
                Candidate("past", 3, ["AnalysisVerification"], 0.7) with
                {
                    SourceType = "PastCaseNote",
                    DocumentId = "past-case",
                    Text = "Past QAC analysis verification",
                    TechnicalTokens = ["QAC"],
                    EstimatedChars = 500,
                },
            ],
        };

        var result = CoverageAwareEvidenceSelector.Select(request);

        Assert.Equal(3, result.Selected.Count);
        Assert.Contains(result.Selected, static item => item.SourceType == "OfficialDoc");
        Assert.Contains(result.Selected, static item => item.SourceType == "Manual");
        Assert.Contains(result.Selected, static item => item.SourceType == "PastCaseNote");
    }

    public static IEnumerable<object[]> FixtureCases() => LoadCases()
        .Select(static item => new object[] { item });

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void Select_MatchesSharedDeterministicFixture(FixtureCase fixture)
    {
        var first = CoverageAwareEvidenceSelector.Select(ToRequest(fixture));
        var second = CoverageAwareEvidenceSelector.Select(ToRequest(fixture));

        Assert.Equal(fixture.ExpectedSelectedIds, first.Selected.Select(static item => item.CandidateId));
        Assert.Equal(fixture.ExpectedSelectedIds, first.Decisions.Select(static item => item.CandidateId));
        Assert.Equal(first.Selected.Select(static item => item.CandidateId),
            second.Selected.Select(static item => item.CandidateId));
    }

    [Fact]
    public void Select_DistinguishesMissingSearchCoverageFromSelectionBudget()
    {
        var missingSearch = CoverageAwareEvidenceSelector.Select(new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = ["A", "B"],
            Candidates = [Candidate("a", 1, ["A"], 0.8)],
        });
        var budgetLimited = CoverageAwareEvidenceSelector.Select(new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = ["A", "B"],
            BaseMaxItems = 1,
            ExpansionMaxItems = 1,
            Candidates = [Candidate("a", 1, ["A"], 0.9), Candidate("b", 2, ["B"], 0.8)],
        });

        Assert.Contains("MissingCoverageInSearchResults", missingSearch.Statuses);
        Assert.DoesNotContain("SelectionBudgetExceeded", missingSearch.Statuses);
        Assert.Contains("SelectionBudgetExceeded", budgetLimited.Statuses);
    }

    [Fact]
    public void Select_ReportsCorpusGapOnlyWhenCorpusCoverageIsProvided()
    {
        var unknownCorpus = CoverageAwareEvidenceSelector.Select(new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = ["A", "B"],
            Candidates = [Candidate("a", 1, ["A"], 0.8)],
        });
        var knownCorpus = CoverageAwareEvidenceSelector.Select(new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = ["A", "B"],
            CorpusCoverage = ["A"],
            Candidates = [Candidate("a", 1, ["A"], 0.8)],
        });

        Assert.DoesNotContain("MissingCoverageInCorpus", unknownCorpus.Statuses);
        Assert.Contains("MissingCoverageInCorpus", knownCorpus.Statuses);
    }

    [Fact]
    public void Select_ManualItemsAreNeverRemovedByLimitOrBudget()
    {
        var result = CoverageAwareEvidenceSelector.Select(new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = ["A"],
            BaseMaxItems = 1,
            ExpansionMaxItems = 1,
            CharacterBudget = 1,
            Candidates =
            [
                Candidate("manual-1", 1, ["A"], 0.1) with { IsManuallySelected = true, EstimatedChars = 10 },
                Candidate("manual-2", 2, [], 0.1) with { IsManuallySelected = true, EstimatedChars = 10 },
            ],
        });

        Assert.Equal(["manual-1", "manual-2"], result.Selected.Select(static item => item.CandidateId));
        Assert.Contains("ManualSelectionExceedsLimit", result.Warnings);
        Assert.Contains("ManualSelectionExceedsCharacterBudget", result.Warnings);
    }

    private static CoverageEvidenceSelectionRequest ToRequest(FixtureCase fixture) => new()
    {
        RequiredCoverage = fixture.RequiredCoverage,
        Candidates = fixture.Candidates.Select((item, index) => new CoverageEvidenceCandidate
        {
            CandidateId = item.Id,
            OriginalRank = item.Rank == 0 ? index + 1 : item.Rank,
            SourceType = item.SourceType ?? "Manual",
            DocumentId = item.DocumentId,
            FilePath = item.FilePath,
            Section = item.Section,
            Text = item.Text ?? item.Id,
            ContentHash = item.ContentHash,
            TechnicalTokens = item.TechnicalTokens,
            Coverage = item.Coverage,
            RankingScore = item.Quality,
            TopicScore = item.Quality,
            EntityScore = item.Quality,
            TechnicalTokenScore = item.Quality,
            SourceTrust = item.Quality,
            VersionScore = item.Quality,
            ExplicitlyExcluded = item.ExplicitlyExcluded,
            TopicConflict = item.TopicConflict,
            ProductMismatch = item.ProductMismatch,
            IsManuallySelected = item.ManuallySelected,
            EstimatedChars = item.EstimatedChars,
        }).ToList(),
        BaseMaxItems = fixture.BaseMaxItems,
        ExpansionMaxItems = fixture.ExpansionMaxItems,
        CharacterBudget = fixture.CharacterBudget,
        MinimumQualityScore = fixture.MinimumQualityScore,
    };

    private static CoverageEvidenceCandidate Candidate(
        string id,
        int rank,
        IReadOnlyList<string> coverage,
        double quality) => new()
    {
        CandidateId = id,
        OriginalRank = rank,
        Text = id,
        Coverage = coverage,
        RankingScore = quality,
        TopicScore = quality,
        EntityScore = quality,
        TechnicalTokenScore = quality,
        SourceTrust = quality,
        VersionScore = quality,
    };

    private static IReadOnlyList<FixtureCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "phase18_coverage_selection_cases.json");
        return JsonSerializer.Deserialize<List<FixtureCase>>(File.ReadAllText(path), JsonOptions) ?? [];
    }

    public sealed record FixtureCase
    {
        public string Id { get; init; } = string.Empty;
        public IReadOnlyList<string> RequiredCoverage { get; init; } = [];
        public int BaseMaxItems { get; init; } = 3;
        public int ExpansionMaxItems { get; init; } = 5;
        public int CharacterBudget { get; init; } = 2000;
        public double MinimumQualityScore { get; init; } = 0.30;
        public IReadOnlyList<string> ExpectedSelectedIds { get; init; } = [];
        public IReadOnlyList<FixtureCandidate> Candidates { get; init; } = [];
    }

    public sealed record FixtureCandidate
    {
        public string Id { get; init; } = string.Empty;
        public int Rank { get; init; }
        public IReadOnlyList<string> Coverage { get; init; } = [];
        public double Quality { get; init; }
        public string? SourceType { get; init; }
        public string? DocumentId { get; init; }
        public string? FilePath { get; init; }
        public string? Section { get; init; }
        public string? Text { get; init; }
        public string? ContentHash { get; init; }
        public IReadOnlyList<string> TechnicalTokens { get; init; } = [];
        public bool ExplicitlyExcluded { get; init; }
        public bool TopicConflict { get; init; }
        public bool ProductMismatch { get; init; }
        public bool ManuallySelected { get; init; }
        public int EstimatedChars { get; init; }
    }
}
