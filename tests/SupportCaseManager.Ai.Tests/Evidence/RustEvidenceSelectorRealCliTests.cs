using System.Text.Json;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class RustEvidenceSelectorRealCliTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void RealRustCli_MatchesCSharpForSharedFixture()
    {
        var executable = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "phase18_coverage_selection_cases.json");
        var cases = JsonSerializer.Deserialize<List<FixtureCase>>(File.ReadAllText(fixturePath), JsonOptions) ?? [];
        Assert.Equal(12, cases.Count);
        var client = new RustEvidenceSelectorClient();

        foreach (var fixture in cases)
        {
            var request = ToRequest(fixture);
            var csharp = CoverageAwareEvidenceSelector.Select(request);
            var rust = client.TrySelect(request, new RustEvidenceSelectorOptions
            {
                UseRustEvidenceSelector = true,
                ExecutablePath = executable,
                TimeoutMs = 2000,
            });

            Assert.True(rust.Success, $"{fixture.Id}: {rust.FailureReason} {rust.Diagnostic}");
            Assert.Equal(csharp.Selected.Select(static item => item.CandidateId), rust.Selection!.Selected.Select(static item => item.CandidateId));
            Assert.Equal(csharp.SelectedCoverage, rust.Selection.SelectedCoverage);
            Assert.Equal(csharp.MissingCoverage, rust.Selection.MissingCoverage);
            Assert.Equal(csharp.Selected.Count, rust.Selection.Selected.Count);
            Assert.Equal(csharp.BudgetLimited, rust.Selection.BudgetLimited);
        }
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

    private sealed record FixtureCase
    {
        public string Id { get; init; } = string.Empty;
        public IReadOnlyList<string> RequiredCoverage { get; init; } = [];
        public int BaseMaxItems { get; init; } = 3;
        public int ExpansionMaxItems { get; init; } = 5;
        public int CharacterBudget { get; init; } = 2000;
        public double MinimumQualityScore { get; init; } = 0.30;
        public IReadOnlyList<FixtureCandidate> Candidates { get; init; } = [];
    }

    private sealed record FixtureCandidate
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
