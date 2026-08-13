using System.Text.Json;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class RustEvidenceSelectorClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Coordinator_OffDoesNotCallRust()
    {
        var rust = new StubClient(_ => throw new InvalidOperationException("must not be called"));

        var execution = CoverageEvidenceSelectorCoordinator.Select(Request(), new RustEvidenceSelectorOptions(), rust);

        Assert.Equal("CSharp", execution.Engine);
        Assert.Equal(["a"], execution.Selection.Selected.Select(static item => item.CandidateId));
        Assert.Equal(0, rust.Calls);
    }

    [Fact]
    public void Client_SuccessUsesUnicodeJsonAndRustResult()
    {
        var runner = new FakeRunner(ProcessSuccess(Output(["a"], ["認証"])));
        var client = new RustEvidenceSelectorClient(runner);

        var result = client.TrySelect(Request("認証"), Options());

        Assert.True(result.Success);
        Assert.Equal(["a"], result.Selection!.Selected.Select(static item => item.CandidateId));
        Assert.Equal(0.02, result.SelectorElapsedMilliseconds, 3);
        using var input = JsonDocument.Parse(runner.InputJson);
        Assert.Equal("認証", input.RootElement.GetProperty("requiredCoverage")[0].GetString());
    }

    public static IEnumerable<object[]> FailureCases()
    {
        yield return [new RustSelectorProcessResult { FailureReason = "StartupFailure" }, "StartupFailure", RustSelectorFailureCategory.StartFailure];
        yield return [new RustSelectorProcessResult { Started = true, TimedOut = true, FailureReason = "Timeout", ProcessTreeTerminated = true }, "Timeout", RustSelectorFailureCategory.Timeout];
        yield return [new RustSelectorProcessResult { Started = true, ExitCode = 2, StandardError = "invalid input" }, "NonZeroExit:2", RustSelectorFailureCategory.NonZeroExit];
        yield return [ProcessSuccess("not-json"), "MalformedJson", RustSelectorFailureCategory.MalformedJson];
        yield return [ProcessSuccess(string.Empty), "EmptyOutput", RustSelectorFailureCategory.EmptyOutput];
        yield return [ProcessSuccess("{}"), "SchemaMismatch", RustSelectorFailureCategory.SchemaMismatch];
        yield return [ProcessSuccess(Output(["unknown"], [])), "UnknownSelectedId", RustSelectorFailureCategory.UnknownEvidenceId];
        yield return [ProcessSuccess(Output(["a", "a"], ["A"])), "DuplicateSelectedId", RustSelectorFailureCategory.DuplicateEvidenceId];
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public void Coordinator_RustFailuresFallBackToCSharp(
        RustSelectorProcessResult process,
        string reason,
        RustSelectorFailureCategory category)
    {
        var client = new RustEvidenceSelectorClient(new FakeRunner(process));

        var execution = CoverageEvidenceSelectorCoordinator.Select(Request(), Options(), client);

        Assert.Equal("RustFallback", execution.Engine);
        Assert.Equal(reason, execution.FallbackReason);
        Assert.Equal(category, client.TrySelect(Request(), Options()).FailureCategory);
        Assert.Equal(["a"], execution.Selection.Selected.Select(static item => item.CandidateId));
    }

    [Fact]
    public void Client_MissingExecutableFallsBackWithoutStartingProcess()
    {
        var runner = new FakeRunner(ProcessSuccess(Output(["a"], ["A"])));
        var client = new RustEvidenceSelectorClient(runner);
        var options = Options() with { ExecutablePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe") };

        var execution = CoverageEvidenceSelectorCoordinator.Select(Request(), options, client);

        Assert.Equal("RustFallback", execution.Engine);
        Assert.Equal("ExecutableMissing", execution.FallbackReason);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Client_InvalidExecutablePathFallsBackWithoutStartingProcess()
    {
        var runner = new FakeRunner(ProcessSuccess(Output(["a"], ["A"])));
        var client = new RustEvidenceSelectorClient(runner);

        var execution = CoverageEvidenceSelectorCoordinator.Select(
            Request(),
            Options() with { ExecutablePath = "bad\0path" },
            client);

        Assert.Equal("RustFallback", execution.Engine);
        Assert.Equal("InvalidExecutablePath", execution.FallbackReason);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Client_RejectsManualSelectionMissingAndExcludedCandidate()
    {
        var manualRequest = Request() with
        {
            Candidates = [Candidate("manual", ["A"]) with { IsManuallySelected = true }],
        };
        var missing = new RustEvidenceSelectorClient(new FakeRunner(ProcessSuccess(Output([], []))))
            .TrySelect(manualRequest, Options());
        var excludedRequest = Request() with
        {
            Candidates = [Candidate("excluded", ["A"]) with { ExplicitlyExcluded = true }],
        };
        var excluded = new RustEvidenceSelectorClient(new FakeRunner(ProcessSuccess(Output(["excluded"], ["A"]))))
            .TrySelect(excludedRequest, Options());

        Assert.Equal("ManualSelectionMissing", missing.FailureReason);
        Assert.Equal(RustSelectorFailureCategory.ManualSelectionViolation, missing.FailureCategory);
        Assert.Equal("ExcludedCandidateSelected", excluded.FailureReason);
        Assert.Equal(RustSelectorFailureCategory.ManualSelectionViolation, excluded.FailureCategory);
    }

    [Fact]
    public void Coordinator_ShadowMatchUsesCSharpAndReportsPassed()
    {
        var expected = CoverageAwareEvidenceSelector.Select(Request());
        var client = new StubClient(_ => new RustEvidenceSelectorAttempt
        {
            Success = true,
            Selection = expected,
            ElapsedMilliseconds = 4,
        });

        var execution = CoverageEvidenceSelectorCoordinator.Select(Request(), Options() with
        {
            UseRustEvidenceSelector = false,
            EnableRustSelectorShadowMode = true,
        }, client);

        Assert.Equal("CSharp", execution.Engine);
        Assert.Equal("passed", execution.ParityValidation);
        Assert.Equal(4, execution.RustElapsedMilliseconds);
        Assert.NotNull(execution.ShadowObservation);
        Assert.Equal(RustSelectorParityStatus.Passed, execution.ShadowObservation.Parity);
    }

    [Fact]
    public void Coordinator_ShadowMismatchNeverUsesRustSelection()
    {
        var mismatch = CoverageAwareEvidenceSelector.Select(Request()) with { Selected = [] };
        var client = new StubClient(_ => new RustEvidenceSelectorAttempt { Success = true, Selection = mismatch });

        var execution = CoverageEvidenceSelectorCoordinator.Select(Request(), Options() with
        {
            UseRustEvidenceSelector = false,
            EnableRustSelectorShadowMode = true,
        }, client);

        Assert.Equal("CSharp", execution.Engine);
        Assert.StartsWith("failed; orderedMatch=false", execution.ParityValidation, StringComparison.Ordinal);
        Assert.Equal(["a"], execution.Selection.Selected.Select(static item => item.CandidateId));
        Assert.DoesNotContain("CSharp=a", execution.ParityValidation, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_PreservesStderrDiagnosticWithoutIncludingInput()
    {
        var client = new RustEvidenceSelectorClient(new FakeRunner(new RustSelectorProcessResult
        {
            Started = true,
            ExitCode = 3,
            StandardError = "selector failed\ninternal category",
        }));

        var attempt = client.TrySelect(Request(), Options());

        Assert.Equal("selector failed internal category", attempt.Diagnostic);
        Assert.DoesNotContain("customer", attempt.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static RustEvidenceSelectorOptions Options() => new()
    {
        UseRustEvidenceSelector = true,
        ExecutablePath = typeof(RustEvidenceSelectorClientTests).Assembly.Location,
        TimeoutMs = 2000,
    };

    private static CoverageEvidenceSelectionRequest Request(string coverage = "A") => new()
    {
        RequiredCoverage = [coverage],
        Candidates = [Candidate("a", [coverage])],
    };

    private static CoverageEvidenceCandidate Candidate(string id, IReadOnlyList<string> coverage) => new()
    {
        CandidateId = id,
        OriginalRank = 1,
        Text = id,
        Coverage = coverage,
        RankingScore = 0.8,
        TopicScore = 0.8,
        EntityScore = 0.8,
        TechnicalTokenScore = 0.8,
        SourceTrust = 0.8,
        VersionScore = 0.8,
    };

    private static RustSelectorProcessResult ProcessSuccess(string output) => new()
    {
        Started = true,
        ExitCode = 0,
        StandardOutput = output,
        ElapsedMilliseconds = 3,
    };

    private static string Output(IReadOnlyList<string> ids, IReadOnlyList<string> coverage) =>
        JsonSerializer.Serialize(new
        {
            selectedEvidenceIds = ids,
            requiredCoverage = coverage,
            searchCoverage = coverage,
            selectedCoverage = coverage,
            missingCoverage = Array.Empty<string>(),
            selectedEvidenceCount = ids.Count,
            redundantCandidatesSkipped = 0,
            budgetLimited = false,
            warnings = Array.Empty<string>(),
            statuses = new[] { "CoverageSatisfied" },
            decisions = ids.Select(id => new
            {
                candidateId = id,
                qualityScore = 0.8,
                setScore = 0.8,
                addedCoverage = coverage,
                isManual = false,
                reason = "QualityAnchor",
            }),
            estimatedChars = ids.Count,
            selectionMode = "CoverageAware",
            selectorElapsedMs = 0.02,
        }, JsonOptions);

    private sealed class FakeRunner(RustSelectorProcessResult result) : IRustSelectorProcessRunner
    {
        public int Calls { get; private set; }
        public string InputJson { get; private set; } = string.Empty;

        public RustSelectorProcessResult Run(string executablePath, string inputJson, int timeoutMs)
        {
            Calls++;
            InputJson = inputJson;
            return result;
        }
    }

    private sealed class StubClient(Func<CoverageEvidenceSelectionRequest, RustEvidenceSelectorAttempt> select) : IRustEvidenceSelectorClient
    {
        public int Calls { get; private set; }

        public RustEvidenceSelectorAttempt TrySelect(CoverageEvidenceSelectionRequest request, RustEvidenceSelectorOptions options)
        {
            Calls++;
            return select(request);
        }
    }
}
