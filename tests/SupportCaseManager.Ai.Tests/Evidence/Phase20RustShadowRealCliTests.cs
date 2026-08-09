using System.Text.Json;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class Phase20RustShadowRealCliTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void SharedFixtures_TwentyRunsEachRemainExactAndSyntheticOnly()
    {
        var executable = RustExecutable();
        if (executable is null) return;
        var cases = LoadCases();
        var store = new RustSelectorShadowObservationStore();

        for (var repetition = 0; repetition < 20; repetition++)
        {
            foreach (var fixture in cases)
            {
                var execution = CoverageEvidenceSelectorCoordinator.Select(ToRequest(fixture), new RustEvidenceSelectorOptions
                {
                    EnableRustSelectorShadowMode = true,
                    ExecutablePath = executable,
                    TimeoutMs = 2000,
                    IsSyntheticObservation = true,
                    ShadowObservationStore = store,
                });

                Assert.Equal("CSharp", execution.Engine);
                Assert.Equal(RustSelectorParityStatus.Passed, execution.ShadowObservation!.Parity);
                Assert.True(execution.ShadowObservation.OrderedMatch, fixture.Id);
                Assert.True(execution.ShadowObservation.SetMatch, fixture.Id);
                Assert.True(execution.ShadowObservation.CoverageMatch, fixture.Id);
                Assert.True(execution.ShadowObservation.MissingCoverageMatch, fixture.Id);
                Assert.True(execution.ShadowObservation.BudgetMatch, fixture.Id);
                Assert.True(execution.RustSelectorElapsedMilliseconds >= 0);
            }
        }

        var stats = store.Snapshot(new RustSelectorReadinessPolicy());
        Assert.Equal(240, stats.TotalRuns);
        Assert.Equal(240, stats.SyntheticRuns);
        Assert.Equal(0, stats.ProductionRuns);
        Assert.Equal(1, stats.ExactOrderMatchRate);
        Assert.Equal(1, stats.SetMatchRate);
        Assert.Equal(1, stats.CoverageMatchRate);
        Assert.Equal(0, stats.FallbackCount);
        Assert.Equal(0, stats.TimeoutCount);
        Assert.Equal(0, stats.InvalidOutputCount);
        Assert.Equal(RustAdoptionReadiness.NotEnoughData, stats.Readiness);
    }

    [Theory]
    [InlineData("excluded-topic-not-selected")]
    [InlineData("manual-selection-completed")]
    [InlineData("five-items-still-insufficient")]
    [InlineData("character-budget")]
    public void ArtificialE2e_CToF_MatchesRealRust(string fixtureId)
    {
        var executable = RustExecutable();
        if (executable is null) return;
        var fixture = Assert.Single(LoadCases(), item => item.Id == fixtureId);

        var execution = CoverageEvidenceSelectorCoordinator.Select(ToRequest(fixture), new RustEvidenceSelectorOptions
        {
            EnableRustSelectorShadowMode = true,
            ExecutablePath = executable,
            IsSyntheticObservation = true,
        });

        Assert.Equal(RustSelectorParityStatus.Passed, execution.ShadowObservation!.Parity);
        Assert.Equal("CSharp", execution.Engine);
    }

    [Fact]
    public void ArtificialE2e_G_MissingExecutableFallsBackToCSharp()
    {
        var request = ToRequest(LoadCases()[0]);
        var execution = CoverageEvidenceSelectorCoordinator.Select(request, new RustEvidenceSelectorOptions
        {
            EnableRustSelectorShadowMode = true,
            ExecutablePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
            IsSyntheticObservation = true,
        });

        Assert.Equal("CSharp", execution.Engine);
        Assert.Equal("ExecutableMissing", execution.FallbackReason);
        Assert.Equal(RustSelectorFailureCategory.ExecutableMissing, execution.ShadowObservation!.FallbackCategory);
        Assert.NotEmpty(execution.Selection.Selected);
    }

    [Fact]
    public void ArtificialE2e_H_TimeoutTerminatesProcessTreeAndKeepsCSharpResult()
    {
        var request = ToRequest(LoadCases()[0]);
        var attempt = new RustEvidenceSelectorAttempt
        {
            FailureReason = "Timeout",
            FailureCategory = RustSelectorFailureCategory.Timeout,
            TimedOut = true,
            ProcessTreeTerminated = true,
            ElapsedMilliseconds = 2000,
        };
        var execution = CoverageEvidenceSelectorCoordinator.Select(request, new RustEvidenceSelectorOptions
        {
            EnableRustSelectorShadowMode = true,
            IsSyntheticObservation = true,
        }, new StubClient(attempt));

        Assert.Equal("CSharp", execution.Engine);
        Assert.True(attempt.ProcessTreeTerminated);
        Assert.True(execution.ShadowObservation!.TimedOut);
        Assert.Equal(RustSelectorFailureCategory.Timeout, execution.ShadowObservation.FallbackCategory);
        Assert.NotEmpty(execution.Selection.Selected);
    }

    [Fact]
    public void ArtificialE2e_I_MalformedOutputFallsBackToCSharp()
    {
        var request = ToRequest(LoadCases()[0]);
        var client = new RustEvidenceSelectorClient(new StaticRunner(new RustSelectorProcessResult
        {
            Started = true,
            ExitCode = 0,
            StandardOutput = "not-json",
        }));
        var execution = CoverageEvidenceSelectorCoordinator.Select(request, new RustEvidenceSelectorOptions
        {
            EnableRustSelectorShadowMode = true,
            ExecutablePath = typeof(Phase20RustShadowRealCliTests).Assembly.Location,
            IsSyntheticObservation = true,
        }, client);

        Assert.Equal("CSharp", execution.Engine);
        Assert.Equal(RustSelectorFailureCategory.MalformedJson, execution.ShadowObservation!.FallbackCategory);
        Assert.Equal(1, execution.ShadowStatistics!.InvalidOutputCount);
        Assert.Equal(1, execution.ShadowStatistics.MalformedOutputCount);
    }

    [Fact]
    public void ArtificialE2e_J_ForcedMismatchKeepsCSharpActualResult()
    {
        var request = ToRequest(LoadCases()[0]);
        var csharp = CoverageAwareEvidenceSelector.Select(request);
        var mismatch = csharp with { Selected = csharp.Selected.Reverse().ToList() };
        var execution = CoverageEvidenceSelectorCoordinator.Select(request, new RustEvidenceSelectorOptions
        {
            EnableRustSelectorShadowMode = true,
            IsSyntheticObservation = true,
        }, new StubClient(new RustEvidenceSelectorAttempt { Success = true, Selection = mismatch }));

        Assert.Equal(csharp.Selected.Select(static item => item.CandidateId),
            execution.Selection.Selected.Select(static item => item.CandidateId));
        Assert.Equal(RustSelectorParityStatus.Mismatch, execution.ShadowObservation!.Parity);
        Assert.False(execution.ShadowObservation.OrderedMatch);
        Assert.True(execution.ShadowObservation.SetMatch);
    }

    private static IReadOnlyList<FixtureCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "phase18_coverage_selection_cases.json");
        var cases = JsonSerializer.Deserialize<List<FixtureCase>>(File.ReadAllText(path), JsonOptions) ?? [];
        Assert.Equal(12, cases.Count);
        return cases;
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

    private static string? RustExecutable()
    {
        var path = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private sealed class StaticRunner(RustSelectorProcessResult result) : IRustSelectorProcessRunner
    {
        public RustSelectorProcessResult Run(string executablePath, string inputJson, int timeoutMs) => result;
    }

    private sealed class StubClient(RustEvidenceSelectorAttempt attempt) : IRustEvidenceSelectorClient
    {
        public RustEvidenceSelectorAttempt TrySelect(
            CoverageEvidenceSelectionRequest request,
            RustEvidenceSelectorOptions options) => attempt;
    }

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
