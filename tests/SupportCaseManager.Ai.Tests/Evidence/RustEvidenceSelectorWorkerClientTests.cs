using System.Text.Json;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class RustEvidenceSelectorWorkerClientTests
{
    [Fact]
    public void Worker_IsLazyAndReusesOneProcessForOneHundredRequests()
    {
        var transport = new FakeTransport();
        using var client = new RustEvidenceSelectorWorkerClient(transport);

        Assert.Equal(0, transport.Starts);
        for (var index = 0; index < 100; index++)
        {
            Assert.True(client.TrySelect(Request(), Options()).Success);
        }

        var health = client.GetHealth();
        Assert.Equal(1, transport.Starts);
        Assert.Equal(100, health.Requests);
        Assert.Equal(1001, health.ProcessId);
        Assert.Equal(RustWorkerHealthStatus.Ready, health.Status);
        Assert.Equal("0.1.0", health.WorkerVersion);
    }

    [Fact]
    public void Worker_MalformedResponseInvalidatesAndRestartsOnNextRequest()
    {
        var transport = new FakeTransport();
        transport.SelectResponses.Enqueue(_ => new RustWorkerTransportExchangeResult
        {
            Success = true,
            ResponseLine = "not-json",
        });
        using var client = new RustEvidenceSelectorWorkerClient(transport);

        var failed = client.TrySelect(Request(), Options());
        var recovered = client.TrySelect(Request(), Options());

        Assert.Equal("WorkerMalformedJson", failed.FailureReason);
        Assert.True(recovered.Success);
        Assert.Equal(2, transport.Starts);
        Assert.Equal(1, client.GetHealth().Restarts);
    }

    [Fact]
    public void Worker_StartupFailureFallsBackAndRestartsOnNextRequest()
    {
        var transport = new FakeTransport();
        transport.StartResponses.Enqueue(new RustWorkerTransportStartResult
        {
            FailureReason = "WorkerStartupFailure",
        });
        using var client = new RustEvidenceSelectorWorkerClient(transport);

        var failed = client.TrySelect(Request(), Options());
        var recovered = client.TrySelect(Request(), Options());

        Assert.Equal("WorkerStartupFailure", failed.FailureReason);
        Assert.True(recovered.Success);
        Assert.Equal(2, transport.Starts);
        Assert.Equal(1, client.GetHealth().Restarts);
    }

    [Fact]
    public void Worker_InvalidHandshakeStopsProcessAndCanRecover()
    {
        var transport = new FakeTransport();
        transport.HelloResponses.Enqueue(new RustWorkerTransportExchangeResult
        {
            Success = true,
            ResponseLine = "{\"operation\":\"hello\",\"protocolVersion\":99,\"version\":\"bad\"}",
        });
        using var client = new RustEvidenceSelectorWorkerClient(transport);

        var failed = client.TrySelect(Request(), Options());
        var recovered = client.TrySelect(Request(), Options());

        Assert.Equal("WorkerHandshakeFailed", failed.FailureReason);
        Assert.True(recovered.Success);
        Assert.Equal(2, transport.Starts);
    }

    [Fact]
    public void Worker_RequestIdMismatchIsRejectedAndProcessIsStopped()
    {
        var transport = new FakeTransport();
        transport.SelectResponses.Enqueue(_ => new RustWorkerTransportExchangeResult
        {
            Success = true,
            ResponseLine = SelectResponse("wrong-id"),
        });
        using var client = new RustEvidenceSelectorWorkerClient(transport);

        var failed = client.TrySelect(Request(), Options());

        Assert.Equal("WorkerRequestIdMismatch", failed.FailureReason);
        Assert.False(transport.IsRunning);
        Assert.Equal(1, client.GetHealth().ProtocolErrors);
    }

    [Fact]
    public void Worker_TimeoutStopsProcessAndUpdatesHealth()
    {
        var transport = new FakeTransport();
        transport.SelectResponses.Enqueue(_ => new RustWorkerTransportExchangeResult
        {
            FailureReason = "WorkerTimeout",
            TimedOut = true,
        });
        using var client = new RustEvidenceSelectorWorkerClient(transport);

        var failed = client.TrySelect(Request(), Options());

        Assert.True(failed.TimedOut);
        Assert.Equal(RustSelectorFailureCategory.Timeout, failed.FailureCategory);
        Assert.True(failed.ProcessTreeTerminated);
        Assert.Equal(1, client.GetHealth().Timeouts);
    }

    [Fact]
    public void Worker_RestartLimitEntersCoolingDown()
    {
        var transport = new FakeTransport();
        transport.SelectResponses.Enqueue(_ => Failure("WorkerStdoutEof"));
        transport.SelectResponses.Enqueue(_ => Failure("WorkerStdoutEof"));
        using var client = new RustEvidenceSelectorWorkerClient(transport);
        var options = Options() with { MaxWorkerRestartsPerMinute = 1 };

        Assert.False(client.TrySelect(Request(), options).Success);
        Assert.False(client.TrySelect(Request(), options).Success);
        var limited = client.TrySelect(Request(), options);

        Assert.Equal("WorkerRestartLimit", limited.FailureReason);
        Assert.Equal(2, transport.Starts);
        Assert.Equal(RustWorkerHealthStatus.CoolingDown, client.GetHealth().Status);
    }

    [Fact]
    public async Task Worker_SerializesConcurrentRequests()
    {
        var transport = new FakeTransport { SelectDelayMilliseconds = 15 };
        using var client = new RustEvidenceSelectorWorkerClient(transport);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => client.TrySelect(Request(), Options()))));

        Assert.All(attempts, static attempt => Assert.True(attempt.Success));
        Assert.Equal(1, transport.MaxConcurrentExchanges);
        Assert.Equal(1, transport.Starts);
    }

    [Fact]
    public void Worker_CanceledBeforeStartDoesNotLaunchProcess()
    {
        var transport = new FakeTransport();
        using var client = new RustEvidenceSelectorWorkerClient(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failed = client.TrySelect(Request(), Options(), cancellation.Token);

        Assert.Equal("Canceled", failed.FailureReason);
        Assert.Equal(0, transport.Starts);
    }

    [Fact]
    public void Worker_DisposeStopsRunningProcess()
    {
        var transport = new FakeTransport();
        var client = new RustEvidenceSelectorWorkerClient(transport);
        Assert.True(client.TrySelect(Request(), Options()).Success);

        client.Dispose();
        client.Dispose();

        Assert.False(transport.IsRunning);
        Assert.True(transport.Stops > 0);
    }

    [Fact]
    public void Coordinator_PersistentSuccessSkipsSingleShot()
    {
        var worker = new StubWorker(Success());
        var singleShot = new StubSingleShot(Success());

        var execution = CoverageEvidenceSelectorCoordinator.Select(
            Request(), PersistentOptions(), singleShot, worker);

        Assert.Equal("PersistentRust", execution.Engine);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(0, singleShot.Calls);
        Assert.Equal(0, worker.Fallbacks);
    }

    [Fact]
    public void Coordinator_WorkerFailureFallsBackToSingleShotForCurrentRequest()
    {
        var worker = new StubWorker(Failed("WorkerTimeout"));
        var singleShot = new StubSingleShot(Success());

        var execution = CoverageEvidenceSelectorCoordinator.Select(
            Request(), PersistentOptions(), singleShot, worker);

        Assert.Equal("Rust", execution.Engine);
        Assert.Equal("Worker:WorkerTimeout", execution.FallbackReason);
        Assert.Equal(1, singleShot.Calls);
        Assert.Equal(1, worker.Fallbacks);
    }

    [Fact]
    public void Coordinator_WorkerAndSingleShotFailuresFallBackToCSharp()
    {
        var worker = new StubWorker(Failed("WorkerStdoutEof"));
        var singleShot = new StubSingleShot(Failed("NonZeroExit:2"));

        var execution = CoverageEvidenceSelectorCoordinator.Select(
            Request(), PersistentOptions(), singleShot, worker);

        Assert.Equal("RustFallback", execution.Engine);
        Assert.Equal("Worker:WorkerStdoutEof; SingleShot:NonZeroExit:2", execution.FallbackReason);
        Assert.Equal(["a"], execution.Selection.Selected.Select(static item => item.CandidateId));
    }

    [Fact]
    public void Coordinator_FeatureOffPreservesExistingPathWithoutTouchingWorker()
    {
        var worker = new StubWorker(throwOnCall: true);
        var singleShot = new StubSingleShot(throwOnCall: true);

        var execution = CoverageEvidenceSelectorCoordinator.Select(
            Request(), new RustEvidenceSelectorOptions(), singleShot, worker);

        Assert.Equal("CSharp", execution.Engine);
        Assert.Equal(0, worker.Calls);
        Assert.Equal(0, singleShot.Calls);
    }

    [Fact]
    public void Coordinator_ShadowUsesPersistentWorkerButKeepsCSharpAnswer()
    {
        var worker = new StubWorker(Success());
        var singleShot = new StubSingleShot(throwOnCall: true);
        var options = PersistentOptions() with
        {
            UseRustEvidenceSelector = false,
            EnableRustSelectorShadowMode = true,
        };

        var execution = CoverageEvidenceSelectorCoordinator.Select(
            Request(), options, singleShot, worker);

        Assert.Equal("CSharp", execution.Engine);
        Assert.Equal("passed", execution.ParityValidation);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(0, singleShot.Calls);
    }

    [Fact]
    public void Worker_HealthDoesNotContainRequestOrCustomerContent()
    {
        var transport = new FakeTransport();
        using var client = new RustEvidenceSelectorWorkerClient(transport);
        Assert.True(client.TrySelect(Request("認証 customer@example.invalid"), Options()).Success);

        var serialized = JsonSerializer.Serialize(client.GetHealth());

        Assert.DoesNotContain("認証", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("customer", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static RustEvidenceSelectorOptions Options() => new()
    {
        UseRustEvidenceSelector = true,
        UsePersistentRustEvidenceSelector = true,
        ExecutablePath = typeof(RustEvidenceSelectorWorkerClientTests).Assembly.Location,
        TimeoutMs = 2_000,
    };

    private static RustEvidenceSelectorOptions PersistentOptions() => Options();

    private static CoverageEvidenceSelectionRequest Request(string coverage = "A") => new()
    {
        RequiredCoverage = [coverage],
        Candidates =
        [
            new CoverageEvidenceCandidate
            {
                CandidateId = "a",
                OriginalRank = 1,
                Text = "evidence",
                Coverage = [coverage],
                RankingScore = 0.8,
                TopicScore = 0.8,
                EntityScore = 0.8,
                TechnicalTokenScore = 0.8,
                SourceTrust = 0.8,
                VersionScore = 0.8,
            },
        ],
    };

    private static RustEvidenceSelectorAttempt Success() => new()
    {
        Success = true,
        Selection = CoverageAwareEvidenceSelector.Select(Request()),
    };

    private static RustEvidenceSelectorAttempt Failed(string reason) => new()
    {
        FailureReason = reason,
        FailureCategory = RustSelectorFailureCategory.StartFailure,
    };

    private static RustWorkerTransportExchangeResult Failure(string reason) => new()
    {
        FailureReason = reason,
    };

    private static string SelectResponse(string requestId, JsonElement? request = null)
    {
        var coverage = request is { } requestElement
            ? requestElement.GetProperty("requiredCoverage").EnumerateArray()
                .Select(static item => item.GetString() ?? string.Empty).ToArray()
            : ["A"];
        var id = request is { } candidateRequest
            ? candidateRequest.GetProperty("candidates")[0].GetProperty("candidateId").GetString() ?? "a"
            : "a";
        return JsonSerializer.Serialize(new
        {
            operation = "select",
            protocolVersion = 1,
            requestId,
            result = new
            {
                selectedEvidenceIds = new[] { id },
                requiredCoverage = coverage,
                searchCoverage = coverage,
                selectedCoverage = coverage,
                missingCoverage = Array.Empty<string>(),
                selectedEvidenceCount = 1,
                redundantCandidatesSkipped = 0,
                budgetLimited = false,
                warnings = Array.Empty<string>(),
                statuses = new[] { "CoverageSatisfied" },
                decisions = new[]
                {
                    new
                    {
                        candidateId = id,
                        qualityScore = 0.8,
                        setScore = 0.8,
                        addedCoverage = coverage,
                        isManual = false,
                        reason = "QualityAnchor",
                    },
                },
                estimatedChars = 8,
                selectionMode = "CoverageAware",
                selectorElapsedMs = 0.02,
            },
        });
    }

    private sealed class FakeTransport : IRustWorkerTransport
    {
        private int activeExchanges;

        public Queue<RustWorkerTransportStartResult> StartResponses { get; } = new();

        public Queue<RustWorkerTransportExchangeResult> HelloResponses { get; } = new();

        public Queue<Func<string, RustWorkerTransportExchangeResult>> SelectResponses { get; } = new();

        public bool IsRunning { get; private set; }

        public int? ProcessId { get; private set; }

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public int MaxConcurrentExchanges { get; private set; }

        public int SelectDelayMilliseconds { get; init; }

        public RustWorkerTransportStartResult Start(string executablePath)
        {
            Starts++;
            if (StartResponses.TryDequeue(out var response) && !response.Success)
            {
                IsRunning = false;
                ProcessId = null;
                return response;
            }
            IsRunning = true;
            ProcessId = 1000 + Starts;
            return new RustWorkerTransportStartResult { Success = true };
        }

        public RustWorkerTransportExchangeResult Exchange(
            string requestLine,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref activeExchanges);
            MaxConcurrentExchanges = Math.Max(MaxConcurrentExchanges, concurrent);
            try
            {
                using var document = JsonDocument.Parse(requestLine);
                var root = document.RootElement;
                var operation = root.GetProperty("operation").GetString();
                if (operation == "hello")
                {
                    if (HelloResponses.TryDequeue(out var helloResponse))
                    {
                        return helloResponse;
                    }
                    return new RustWorkerTransportExchangeResult
                    {
                        Success = true,
                        ResponseLine = "{\"operation\":\"hello\",\"protocolVersion\":1,\"version\":\"0.1.0\"}",
                    };
                }
                if (SelectDelayMilliseconds > 0)
                {
                    Thread.Sleep(SelectDelayMilliseconds);
                }
                if (SelectResponses.TryDequeue(out var response))
                {
                    return response(requestLine);
                }
                var requestId = root.GetProperty("requestId").GetString()!;
                return new RustWorkerTransportExchangeResult
                {
                    Success = true,
                    ResponseLine = SelectResponse(requestId, root.GetProperty("request")),
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeExchanges);
            }
        }

        public void Stop(int gracefulTimeoutMs)
        {
            Stops++;
            IsRunning = false;
            ProcessId = null;
        }

        public void Dispose() => Stop(0);
    }

    private sealed class StubSingleShot : IRustEvidenceSelectorClient
    {
        private readonly RustEvidenceSelectorAttempt attempt;
        private readonly bool throwOnCall;

        public StubSingleShot(RustEvidenceSelectorAttempt? attempt = null, bool throwOnCall = false)
        {
            this.attempt = attempt ?? Failed("unused");
            this.throwOnCall = throwOnCall;
        }

        public int Calls { get; private set; }

        public RustEvidenceSelectorAttempt TrySelect(
            CoverageEvidenceSelectionRequest request,
            RustEvidenceSelectorOptions options)
        {
            Calls++;
            return throwOnCall ? throw new InvalidOperationException("must not be called") : attempt;
        }
    }

    private sealed class StubWorker : IRustEvidenceSelectorWorkerClient
    {
        private readonly RustEvidenceSelectorAttempt attempt;
        private readonly bool throwOnCall;

        public StubWorker(RustEvidenceSelectorAttempt? attempt = null, bool throwOnCall = false)
        {
            this.attempt = attempt ?? Failed("unused");
            this.throwOnCall = throwOnCall;
        }

        public int Calls { get; private set; }

        public int Fallbacks { get; private set; }

        public RustEvidenceSelectorAttempt TrySelect(
            CoverageEvidenceSelectionRequest request,
            RustEvidenceSelectorOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return throwOnCall ? throw new InvalidOperationException("must not be called") : attempt;
        }

        public RustEvidenceSelectorWorkerHealth GetHealth() => new()
        {
            Status = RustWorkerHealthStatus.Ready,
            Requests = Calls,
            Fallbacks = Fallbacks,
        };

        public void RecordFallback() => Fallbacks++;

        public void Dispose()
        {
        }
    }
}
