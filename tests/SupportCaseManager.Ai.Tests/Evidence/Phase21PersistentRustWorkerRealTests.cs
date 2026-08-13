using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class Phase21PersistentRustWorkerRealTests
{
    [Fact]
    public void RealWorker_FiveCasesAndOneHundredRequestsReuseOnePid()
    {
        var executable = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        using var worker = new RustEvidenceSelectorWorkerClient();
        var options = new RustEvidenceSelectorOptions
        {
            UseRustEvidenceSelector = true,
            UsePersistentRustEvidenceSelector = true,
            ExecutablePath = executable,
            TimeoutMs = 2_000,
            MaxWorkerRestartsPerMinute = 3,
        };
        int? processId = null;
        for (var index = 0; index < 100; index++)
        {
            var request = Request(index % 5);
            var expected = CoverageAwareEvidenceSelector.Select(request);
            var actual = worker.TrySelect(request, options);

            Assert.True(actual.Success, $"request {index}: {actual.FailureReason} {actual.Diagnostic}");
            Assert.Equal(
                expected.Selected.Select(static item => item.CandidateId),
                actual.Selection!.Selected.Select(static item => item.CandidateId));
            Assert.Equal(expected.SelectedCoverage, actual.Selection.SelectedCoverage);
            var currentPid = worker.GetHealth().ProcessId;
            processId ??= currentPid;
            Assert.Equal(processId, currentPid);
        }

        var health = worker.GetHealth();
        Assert.Equal(100, health.Requests);
        Assert.Equal(0, health.Restarts);
        Assert.Equal(RustWorkerHealthStatus.Ready, health.Status);
        Assert.Equal(
            RustPersistentWorkerAdoptionReadiness.Ready,
            new RustPersistentWorkerReadinessPolicy().Evaluate(
                health,
                parityConfirmed: true,
                processReuseConfirmed: true,
                benchmarkImproved: true,
                orphanProcessDetected: false));
        worker.Dispose();
        var stopped = worker.GetHealth();
        Assert.Equal(RustWorkerHealthStatus.Stopped, stopped.Status);
        Assert.Null(stopped.ProcessId);
    }

    [Fact]
    public void ReadinessPolicy_CentralizesReadyInvestigationAndBlockedThresholds()
    {
        var policy = new RustPersistentWorkerReadinessPolicy();
        var readyHealth = new RustEvidenceSelectorWorkerHealth
        {
            Status = RustWorkerHealthStatus.Ready,
            Requests = 100,
        };

        Assert.Equal(
            RustPersistentWorkerAdoptionReadiness.Ready,
            policy.Evaluate(readyHealth, true, true, true, false));
        Assert.Equal(
            RustPersistentWorkerAdoptionReadiness.NeedsInvestigation,
            policy.Evaluate(readyHealth with { Requests = 99 }, true, true, true, false));
        Assert.Equal(
            RustPersistentWorkerAdoptionReadiness.Blocked,
            policy.Evaluate(readyHealth with { ProtocolErrors = 1 }, true, true, true, false));
    }

    private static CoverageEvidenceSelectionRequest Request(int variant)
    {
        var coverage = $"Coverage-{variant}";
        return new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = [coverage],
            Candidates =
            [
                Candidate($"best-{variant}", coverage, 0.90),
                Candidate($"second-{variant}", coverage, 0.60),
            ],
            BaseMaxItems = 1,
            ExpansionMaxItems = 1,
            CharacterBudget = 2_000,
            MinimumQualityScore = 0.30,
        };
    }

    private static CoverageEvidenceCandidate Candidate(string id, string coverage, double score) => new()
    {
        CandidateId = id,
        OriginalRank = score > 0.8 ? 1 : 2,
        SourceType = "Manual",
        Text = $"{coverage} の人工テスト根拠",
        Coverage = [coverage],
        RankingScore = score,
        TopicScore = score,
        EntityScore = score,
        TechnicalTokenScore = score,
        SourceTrust = score,
        VersionScore = score,
        EstimatedChars = 32,
    };
}
