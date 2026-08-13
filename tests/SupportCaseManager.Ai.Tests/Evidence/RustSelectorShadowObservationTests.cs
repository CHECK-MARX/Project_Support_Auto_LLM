using System.Text.Json;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class RustSelectorShadowObservationTests
{
    [Fact]
    public void Aggregation_ComputesRatesMedianP95AndReady()
    {
        var records = Enumerable.Range(1, 50)
            .Select(index => Matched() with
            {
                CSharpElapsedMilliseconds = index / 10.0,
                RustProcessElapsedMilliseconds = index,
                RustSelectorElapsedMilliseconds = index / 100.0,
            })
            .ToList();

        var stats = RustSelectorShadowObservationStore.Calculate(records, new RustSelectorReadinessPolicy());

        Assert.Equal(50, stats.ProductionRuns);
        Assert.Equal(1, stats.ExactOrderMatchRate);
        Assert.Equal(1, stats.SetMatchRate);
        Assert.Equal(1, stats.CoverageMatchRate);
        Assert.Equal(25, stats.RustMedianElapsedMilliseconds);
        Assert.Equal(48, stats.RustP95ElapsedMilliseconds);
        Assert.Equal(2.5, stats.CSharpMedianElapsedMilliseconds, 3);
        Assert.Equal(4.8, stats.CSharpP95ElapsedMilliseconds, 3);
        Assert.Equal(24.75, stats.EstimatedProcessOverheadMilliseconds, 3);
        Assert.Equal(RustAdoptionReadiness.Ready, stats.Readiness);
    }

    [Fact]
    public void Readiness_ArtificialRunsNeverMakeProductionReady()
    {
        var records = Enumerable.Range(0, 240).Select(_ => Matched() with { IsSynthetic = true }).ToList();

        var stats = RustSelectorShadowObservationStore.Calculate(records, new RustSelectorReadinessPolicy());

        Assert.Equal(0, stats.ProductionRuns);
        Assert.Equal(240, stats.SyntheticRuns);
        Assert.Equal(RustAdoptionReadiness.NotEnoughData, stats.Readiness);
    }

    [Fact]
    public void Readiness_MismatchNeedsInvestigationAndTimeoutBlocks()
    {
        var mismatch = Enumerable.Range(0, 50).Select(_ => Matched()).ToList();
        mismatch[^1] = mismatch[^1] with { OrderedMatch = false, Parity = RustSelectorParityStatus.Mismatch };
        var timeout = Enumerable.Range(0, 50).Select(_ => Matched()).ToList();
        timeout[^1] = timeout[^1] with
        {
            TimedOut = true,
            FallbackOccurred = true,
            FallbackCategory = RustSelectorFailureCategory.Timeout,
            Parity = RustSelectorParityStatus.Fallback,
        };

        var mismatchStats = RustSelectorShadowObservationStore.Calculate(mismatch, new RustSelectorReadinessPolicy());
        var timeoutStats = RustSelectorShadowObservationStore.Calculate(timeout, new RustSelectorReadinessPolicy());

        Assert.Equal(RustAdoptionReadiness.NeedsInvestigation, mismatchStats.Readiness);
        Assert.Equal(1, mismatchStats.ConsecutiveMismatchCount);
        Assert.Equal(RustAdoptionReadiness.Blocked, timeoutStats.Readiness);
        Assert.Equal(1, timeoutStats.TimeoutCount);
    }

    [Fact]
    public void Store_EnforcesMaximumRecords()
    {
        var store = new RustSelectorShadowObservationStore();
        var policy = new RustSelectorReadinessPolicy { MaxStoredRecords = 50 };
        for (var index = 0; index < 75; index++)
        {
            store.Record(Matched() with { Timestamp = DateTimeOffset.UtcNow.AddSeconds(index) }, policy);
        }

        var stats = store.Snapshot(policy);

        Assert.Equal(50, stats.TotalRuns);
    }

    [Fact]
    public void Store_RemovesRecordsPastRetentionPeriod()
    {
        var store = new RustSelectorShadowObservationStore();
        var policy = new RustSelectorReadinessPolicy { RetentionDays = 30 };
        store.Record(Matched() with { Timestamp = DateTimeOffset.UtcNow.AddDays(-31) }, policy);
        store.Record(Matched(), policy);

        var stats = store.Snapshot(policy);

        Assert.Equal(1, stats.TotalRuns);
    }

    [Fact]
    public void Readiness_FallbackRateAboveOnePercentNeedsInvestigation()
    {
        var records = Enumerable.Range(0, 100).Select(_ => Matched()).ToList();
        records[^1] = records[^1] with
        {
            FallbackOccurred = true,
            FallbackCategory = RustSelectorFailureCategory.StartFailure,
            Parity = RustSelectorParityStatus.Fallback,
        };
        records[^2] = records[^2] with
        {
            FallbackOccurred = true,
            FallbackCategory = RustSelectorFailureCategory.StartFailure,
            Parity = RustSelectorParityStatus.Fallback,
        };

        var stats = RustSelectorShadowObservationStore.Calculate(records, new RustSelectorReadinessPolicy());

        Assert.Equal(0.02, stats.FallbackRate, 3);
        Assert.Equal(RustAdoptionReadiness.NeedsInvestigation, stats.Readiness);
    }

    [Fact]
    public void ObservationSerialization_ContainsOnlyHashedIdsAndNoCustomerContent()
    {
        const string sensitiveId = "Acme-Customer-Taro@example.com-evidence";
        var csharp = Selection(sensitiveId);
        var rust = Selection("different-id");
        var store = new RustSelectorShadowObservationStore();
        var execution = CoverageEvidenceSelectorCoordinator.Select(Request(sensitiveId), ShadowOptions(store),
            new StubClient(new RustEvidenceSelectorAttempt { Success = true, Selection = rust }));

        var json = JsonSerializer.Serialize(execution.ShadowObservation);

        Assert.Equal([RustSelectorPrivacy.HashEvidenceId(sensitiveId)], execution.ShadowObservation!.CSharpSelectedIdHashes);
        Assert.DoesNotContain(sensitiveId, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inquiry", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidenceText", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShadowFailure_UsesCSharpAndRecordsTimeoutWithoutThrowing()
    {
        var store = new RustSelectorShadowObservationStore();
        var client = new StubClient(new RustEvidenceSelectorAttempt
        {
            FailureReason = "Timeout",
            FailureCategory = RustSelectorFailureCategory.Timeout,
            TimedOut = true,
            ProcessTreeTerminated = true,
            ElapsedMilliseconds = 2000,
        });

        var execution = CoverageEvidenceSelectorCoordinator.Select(Request("a"), ShadowOptions(store), client);

        Assert.Equal("CSharp", execution.Engine);
        Assert.Equal(["a"], execution.Selection.Selected.Select(static item => item.CandidateId));
        Assert.Equal(RustSelectorParityStatus.Fallback, execution.ShadowObservation!.Parity);
        Assert.True(execution.ShadowObservation.TimedOut);
        Assert.True(client.Attempt.ProcessTreeTerminated);
        Assert.Equal(1, execution.ShadowStatistics!.FallbackCount);
        Assert.Equal(1, execution.ShadowStatistics.TimeoutCount);
    }

    [Theory]
    [InlineData("MalformedJson", RustSelectorFailureCategory.MalformedJson)]
    [InlineData("UnknownSelectedId", RustSelectorFailureCategory.UnknownEvidenceId)]
    [InlineData("DuplicateSelectedId", RustSelectorFailureCategory.DuplicateEvidenceId)]
    [InlineData("ManualSelectionMissing", RustSelectorFailureCategory.ManualSelectionViolation)]
    public void ShadowFailure_PreservesClassifiedReason(string reason, RustSelectorFailureCategory category)
    {
        var execution = CoverageEvidenceSelectorCoordinator.Select(Request("a"), ShadowOptions(),
            new StubClient(new RustEvidenceSelectorAttempt { FailureReason = reason, FailureCategory = category }));

        Assert.Equal(category, execution.ShadowObservation!.FallbackCategory);
        Assert.Equal("CSharp", execution.Engine);
    }

    [Fact]
    public void FileStore_PersistsBoundedPrivacySafeRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phase20-shadow-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "shadow.json");
        try
        {
            var store = new RustSelectorShadowObservationStore(path);
            store.Record(Matched(), new RustSelectorReadinessPolicy());
            var text = File.ReadAllText(path);

            Assert.DoesNotContain("text", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, new RustSelectorShadowObservationStore(path)
                .Snapshot(new RustSelectorReadinessPolicy()).TotalRuns);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RustSelectorShadowObservation Matched() => new()
    {
        OrderedMatch = true,
        SetMatch = true,
        CoverageMatch = true,
        MissingCoverageMatch = true,
        BudgetMatch = true,
        Parity = RustSelectorParityStatus.Passed,
        CSharpElapsedMilliseconds = 0.04,
        RustProcessElapsedMilliseconds = 96,
        RustSelectorElapsedMilliseconds = 0.02,
    };

    private static RustEvidenceSelectorOptions ShadowOptions(IRustSelectorShadowObservationStore? store = null) => new()
    {
        EnableRustSelectorShadowMode = true,
        UseRustEvidenceSelector = false,
        ShadowObservationStore = store,
        ShadowMinimumRunsForReadiness = 50,
        ShadowMaxStoredRecords = 500,
    };

    private static CoverageEvidenceSelectionRequest Request(string id) => new()
    {
        RequiredCoverage = ["A"],
        Candidates = [Candidate(id)],
    };

    private static CoverageEvidenceCandidate Candidate(string id) => new()
    {
        CandidateId = id,
        OriginalRank = 1,
        Text = "sensitive body is deliberately not copied to observations",
        Coverage = ["A"],
        RankingScore = 0.8,
        TopicScore = 0.8,
        EntityScore = 0.8,
        TechnicalTokenScore = 0.8,
        SourceTrust = 0.8,
        VersionScore = 0.8,
    };

    private static CoverageEvidenceSelectionResult Selection(string id) => new()
    {
        Selected = [Candidate(id)],
        RequiredCoverage = ["A"],
        SearchCoverage = ["A"],
        SelectedCoverage = ["A"],
        MissingCoverage = [],
        BudgetLimited = false,
    };

    private sealed class StubClient(RustEvidenceSelectorAttempt attempt) : IRustEvidenceSelectorClient
    {
        public RustEvidenceSelectorAttempt Attempt => attempt;

        public RustEvidenceSelectorAttempt TrySelect(
            CoverageEvidenceSelectionRequest request,
            RustEvidenceSelectorOptions options) => attempt;
    }
}
