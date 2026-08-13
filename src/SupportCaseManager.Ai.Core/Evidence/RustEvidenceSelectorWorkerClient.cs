using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Evidence;

public enum RustWorkerHealthStatus
{
    Stopped,
    Starting,
    Ready,
    Busy,
    Faulted,
    CoolingDown,
}

public sealed record RustEvidenceSelectorWorkerHealth
{
    public string Mode { get; init; } = "PersistentWorker";

    public RustWorkerHealthStatus Status { get; init; }

    public int? ProcessId { get; init; }

    public long Requests { get; init; }

    public int Restarts { get; init; }

    public int Fallbacks { get; init; }

    public int ProtocolErrors { get; init; }

    public int Timeouts { get; init; }

    public double MedianElapsedMilliseconds { get; init; }

    public double P95ElapsedMilliseconds { get; init; }

    public double P99ElapsedMilliseconds { get; init; }

    public string WorkerVersion { get; init; } = string.Empty;

    public int ProtocolVersion { get; init; } = RustEvidenceSelectorWorkerClient.ProtocolVersion;
}

public enum RustPersistentWorkerAdoptionReadiness
{
    Ready,
    NeedsInvestigation,
    Blocked,
}

public sealed record RustPersistentWorkerReadinessPolicy
{
    public int MinimumSuccessfulRequests { get; init; } = 100;

    public int MaximumRestarts { get; init; }

    public int MaximumFallbacks { get; init; }

    public int MaximumProtocolErrors { get; init; }

    public int MaximumTimeouts { get; init; }

    public RustPersistentWorkerAdoptionReadiness Evaluate(
        RustEvidenceSelectorWorkerHealth health,
        bool parityConfirmed,
        bool processReuseConfirmed,
        bool benchmarkImproved,
        bool orphanProcessDetected)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (!parityConfirmed || orphanProcessDetected ||
            health.ProtocolErrors > MaximumProtocolErrors ||
            health.Timeouts > MaximumTimeouts)
        {
            return RustPersistentWorkerAdoptionReadiness.Blocked;
        }

        if (health.Requests < MinimumSuccessfulRequests ||
            health.Restarts > MaximumRestarts ||
            health.Fallbacks > MaximumFallbacks ||
            !processReuseConfirmed ||
            !benchmarkImproved)
        {
            return RustPersistentWorkerAdoptionReadiness.NeedsInvestigation;
        }

        return RustPersistentWorkerAdoptionReadiness.Ready;
    }
}

public sealed record RustWorkerTransportStartResult
{
    public bool Success { get; init; }

    public string FailureReason { get; init; } = string.Empty;
}

public sealed record RustWorkerTransportExchangeResult
{
    public bool Success { get; init; }

    public string ResponseLine { get; init; } = string.Empty;

    public string FailureReason { get; init; } = string.Empty;

    public bool TimedOut { get; init; }

    public bool Canceled { get; init; }
}

public interface IRustWorkerTransport : IDisposable
{
    bool IsRunning { get; }

    int? ProcessId { get; }

    RustWorkerTransportStartResult Start(string executablePath);

    RustWorkerTransportExchangeResult Exchange(
        string requestLine,
        int timeoutMs,
        CancellationToken cancellationToken);

    void Stop(int gracefulTimeoutMs);
}

public sealed class RustWorkerProcessTransport : IRustWorkerTransport
{
    private Process? process;

    public bool IsRunning => process is { HasExited: false };

    public int? ProcessId => IsRunning ? process!.Id : null;

    public RustWorkerTransportStartResult Start(string executablePath)
    {
        Stop(0);
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--worker",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            process.ErrorDataReceived += static (_, _) => { };
            if (!process.Start())
            {
                Stop(0);
                return new RustWorkerTransportStartResult { FailureReason = "WorkerStartupFailure" };
            }
            process.BeginErrorReadLine();
            return new RustWorkerTransportStartResult { Success = true };
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException)
        {
            Stop(0);
            return new RustWorkerTransportStartResult
            {
                FailureReason = $"WorkerStartupFailure:{exception.GetType().Name}",
            };
        }
    }

    public RustWorkerTransportExchangeResult Exchange(
        string requestLine,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return new RustWorkerTransportExchangeResult { FailureReason = "WorkerNotRunning" };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(timeoutMs, 100, 30_000));
        try
        {
            process!.StandardInput.WriteLineAsync(requestLine.AsMemory(), timeout.Token)
                .GetAwaiter().GetResult();
            process.StandardInput.FlushAsync(timeout.Token).GetAwaiter().GetResult();
            var response = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask()
                .GetAwaiter().GetResult();
            return response is null
                ? new RustWorkerTransportExchangeResult { FailureReason = "WorkerStdoutEof" }
                : new RustWorkerTransportExchangeResult { Success = true, ResponseLine = response };
        }
        catch (OperationCanceledException)
        {
            return new RustWorkerTransportExchangeResult
            {
                FailureReason = cancellationToken.IsCancellationRequested ? "Canceled" : "WorkerTimeout",
                TimedOut = !cancellationToken.IsCancellationRequested,
                Canceled = cancellationToken.IsCancellationRequested,
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
        {
            return new RustWorkerTransportExchangeResult
            {
                FailureReason = $"WorkerIoFailure:{exception.GetType().Name}",
            };
        }
    }

    public void Stop(int gracefulTimeoutMs)
    {
        var current = process;
        process = null;
        if (current is null)
        {
            return;
        }

        try
        {
            if (!current.HasExited)
            {
                try
                {
                    current.StandardInput.Close();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
                if (!current.WaitForExit(Math.Clamp(gracefulTimeoutMs, 0, 5_000)))
                {
                    current.Kill(entireProcessTree: true);
                    current.WaitForExit(1_000);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            current.Dispose();
        }
    }

    public void Dispose() => Stop(1_000);
}

public interface IRustEvidenceSelectorWorkerClient : IDisposable
{
    RustEvidenceSelectorAttempt TrySelect(
        CoverageEvidenceSelectionRequest request,
        RustEvidenceSelectorOptions options,
        CancellationToken cancellationToken = default);

    RustEvidenceSelectorWorkerHealth GetHealth();

    void RecordFallback();
}

public sealed class RustEvidenceSelectorWorkerClient : IRustEvidenceSelectorWorkerClient
{
    public const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxTimingSamples = 2_000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IRustWorkerTransport transport;
    private readonly Queue<DateTimeOffset> restartTimes = new();
    private readonly List<double> elapsedSamples = [];
    private RustWorkerHealthStatus status = RustWorkerHealthStatus.Stopped;
    private bool hasAttemptedStart;
    private bool disposed;
    private long requests;
    private int restarts;
    private int fallbacks;
    private int protocolErrors;
    private int timeouts;
    private string workerVersion = string.Empty;
    private string? runningExecutablePath;

    public RustEvidenceSelectorWorkerClient(IRustWorkerTransport? transport = null)
    {
        this.transport = transport ?? new RustWorkerProcessTransport();
    }

    public RustEvidenceSelectorAttempt TrySelect(
        CoverageEvidenceSelectionRequest request,
        RustEvidenceSelectorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            gate.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return RustEvidenceSelectorClient.CreateFailure("Canceled", category: RustSelectorFailureCategory.UnexpectedException);
        }

        try
        {
            if (disposed)
            {
                return RustEvidenceSelectorClient.CreateFailure("WorkerDisposed",
                    category: RustSelectorFailureCategory.StartFailure);
            }
            string? executablePath;
            try
            {
                executablePath = RustEvidenceSelectorClient.ResolveExecutablePath(options.ExecutablePath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return RustEvidenceSelectorClient.CreateFailure("InvalidExecutablePath",
                    category: RustSelectorFailureCategory.InvalidExecutablePath);
            }
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return RustEvidenceSelectorClient.CreateFailure("ExecutableMissing",
                    category: RustSelectorFailureCategory.ExecutableMissing);
            }

            if (!EnsureStarted(executablePath, options, cancellationToken, out var startFailure))
            {
                return startFailure!;
            }

            status = RustWorkerHealthStatus.Busy;
            requests++;
            var requestId = Guid.NewGuid().ToString("N");
            var line = JsonSerializer.Serialize(new
            {
                operation = "select",
                protocolVersion = ProtocolVersion,
                requestId,
                request,
            }, JsonOptions);
            var exchange = transport.Exchange(line, options.TimeoutMs, cancellationToken);
            if (!exchange.Success)
            {
                var category = exchange.TimedOut
                    ? RustSelectorFailureCategory.Timeout
                    : RustSelectorFailureCategory.StartFailure;
                if (exchange.TimedOut)
                {
                    timeouts++;
                }
                InvalidateWorker(RustWorkerHealthStatus.Faulted);
                return RustEvidenceSelectorClient.CreateFailure(
                    exchange.FailureReason,
                    stopwatch.ElapsedMilliseconds,
                    timedOut: exchange.TimedOut,
                    category: category,
                    processTreeTerminated: true);
            }

            WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<WorkerResponse>(exchange.ResponseLine, JsonOptions);
            }
            catch (JsonException)
            {
                protocolErrors++;
                InvalidateWorker(RustWorkerHealthStatus.Faulted);
                return RustEvidenceSelectorClient.CreateFailure("WorkerMalformedJson", stopwatch.ElapsedMilliseconds,
                    category: RustSelectorFailureCategory.MalformedJson, processTreeTerminated: true);
            }
            if (response is null || response.ProtocolVersion != ProtocolVersion ||
                !string.Equals(response.Operation, "select", StringComparison.Ordinal) ||
                !string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ||
                response.Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                protocolErrors++;
                InvalidateWorker(RustWorkerHealthStatus.Faulted);
                var reason = response?.RequestId is not null && !string.Equals(response.RequestId, requestId, StringComparison.Ordinal)
                    ? "WorkerRequestIdMismatch"
                    : "WorkerProtocolMismatch";
                return RustEvidenceSelectorClient.CreateFailure(reason, stopwatch.ElapsedMilliseconds,
                    category: RustSelectorFailureCategory.SchemaMismatch, processTreeTerminated: true);
            }

            RustEvidenceSelectorClient.RustSelectorOutput? output;
            try
            {
                output = response.Result.Deserialize<RustEvidenceSelectorClient.RustSelectorOutput>(JsonOptions);
            }
            catch (JsonException)
            {
                output = null;
            }
            if (output is null)
            {
                protocolErrors++;
                InvalidateWorker(RustWorkerHealthStatus.Faulted);
                return RustEvidenceSelectorClient.CreateFailure("WorkerSchemaMismatch", stopwatch.ElapsedMilliseconds,
                    category: RustSelectorFailureCategory.SchemaMismatch, processTreeTerminated: true);
            }

            stopwatch.Stop();
            status = RustWorkerHealthStatus.Ready;
            RecordElapsed(stopwatch.Elapsed.TotalMilliseconds);
            return RustEvidenceSelectorClient.ValidateOutput(request, output, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            gate.Release();
        }
    }

    public RustEvidenceSelectorWorkerHealth GetHealth()
    {
        gate.Wait();
        try
        {
            var timings = elapsedSamples.Order().ToList();
            return new RustEvidenceSelectorWorkerHealth
            {
                Status = status,
                ProcessId = transport.ProcessId,
                Requests = requests,
                Restarts = restarts,
                Fallbacks = fallbacks,
                ProtocolErrors = protocolErrors,
                Timeouts = timeouts,
                MedianElapsedMilliseconds = Percentile(timings, 0.50),
                P95ElapsedMilliseconds = Percentile(timings, 0.95),
                P99ElapsedMilliseconds = Percentile(timings, 0.99),
                WorkerVersion = workerVersion,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public void RecordFallback()
    {
        Interlocked.Increment(ref fallbacks);
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            transport.Stop(1_000);
            status = RustWorkerHealthStatus.Stopped;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool EnsureStarted(
        string executablePath,
        RustEvidenceSelectorOptions options,
        CancellationToken cancellationToken,
        out RustEvidenceSelectorAttempt? failure)
    {
        failure = null;
        if (transport.IsRunning && string.Equals(runningExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (transport.IsRunning)
        {
            transport.Stop(1_000);
        }
        if (hasAttemptedStart && !CanRestart(options.MaxWorkerRestartsPerMinute))
        {
            status = RustWorkerHealthStatus.CoolingDown;
            failure = RustEvidenceSelectorClient.CreateFailure("WorkerRestartLimit",
                category: RustSelectorFailureCategory.StartFailure);
            return false;
        }

        status = RustWorkerHealthStatus.Starting;
        if (hasAttemptedStart)
        {
            restarts++;
            restartTimes.Enqueue(DateTimeOffset.UtcNow);
        }
        hasAttemptedStart = true;
        var start = transport.Start(executablePath);
        if (!start.Success)
        {
            status = RustWorkerHealthStatus.Faulted;
            failure = RustEvidenceSelectorClient.CreateFailure(start.FailureReason,
                category: RustSelectorFailureCategory.StartFailure);
            return false;
        }
        runningExecutablePath = executablePath;

        var hello = JsonSerializer.Serialize(new
        {
            operation = "hello",
            protocolVersion = ProtocolVersion,
        }, JsonOptions);
        var exchange = transport.Exchange(hello, options.TimeoutMs, cancellationToken);
        if (!exchange.Success)
        {
            if (exchange.TimedOut)
            {
                timeouts++;
            }
            InvalidateWorker(RustWorkerHealthStatus.Faulted);
            failure = RustEvidenceSelectorClient.CreateFailure(exchange.FailureReason,
                timedOut: exchange.TimedOut,
                category: exchange.TimedOut ? RustSelectorFailureCategory.Timeout : RustSelectorFailureCategory.StartFailure,
                processTreeTerminated: true);
            return false;
        }

        WorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<WorkerResponse>(exchange.ResponseLine, JsonOptions);
        }
        catch (JsonException)
        {
            response = null;
        }
        if (response is null || !string.Equals(response.Operation, "hello", StringComparison.Ordinal) ||
            response.ProtocolVersion != ProtocolVersion || string.IsNullOrWhiteSpace(response.Version))
        {
            protocolErrors++;
            InvalidateWorker(RustWorkerHealthStatus.Faulted);
            failure = RustEvidenceSelectorClient.CreateFailure("WorkerHandshakeFailed",
                category: RustSelectorFailureCategory.SchemaMismatch, processTreeTerminated: true);
            return false;
        }
        workerVersion = response.Version;
        status = RustWorkerHealthStatus.Ready;
        return true;
    }

    private bool CanRestart(int configuredLimit)
    {
        var limit = Math.Clamp(configuredLimit, 0, 60);
        var threshold = DateTimeOffset.UtcNow.AddMinutes(-1);
        while (restartTimes.TryPeek(out var timestamp) && timestamp < threshold)
        {
            restartTimes.Dequeue();
        }
        return restartTimes.Count < limit;
    }

    private void InvalidateWorker(RustWorkerHealthStatus newStatus)
    {
        transport.Stop(250);
        runningExecutablePath = null;
        status = newStatus;
    }

    private void RecordElapsed(double elapsed)
    {
        elapsedSamples.Add(elapsed);
        if (elapsedSamples.Count > MaxTimingSamples)
        {
            elapsedSamples.RemoveRange(0, elapsedSamples.Count - MaxTimingSamples);
        }
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile) =>
        values.Count == 0 ? 0 : values[(int)Math.Ceiling((values.Count - 1) * percentile)];

    private sealed record WorkerResponse
    {
        public string Operation { get; init; } = string.Empty;

        public int ProtocolVersion { get; init; }

        public string? RequestId { get; init; }

        public string Version { get; init; } = string.Empty;

        public JsonElement Result { get; init; }
    }
}
