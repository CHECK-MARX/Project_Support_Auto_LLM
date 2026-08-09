using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Evidence;

public sealed record RustEvidenceSelectorOptions
{
    public bool UseRustEvidenceSelector { get; init; }

    public bool EnableRustSelectorShadowMode { get; init; }

    public int TimeoutMs { get; init; } = 2000;

    public string? ExecutablePath { get; init; }

    public string RankingMode { get; init; } = "CoverageAware";

    public bool IsSyntheticObservation { get; init; }

    public string? ShadowObservationFilePath { get; init; }

    public int ShadowMinimumRunsForReadiness { get; init; } = 50;

    public int ShadowMaxStoredRecords { get; init; } = 500;

    public IRustSelectorShadowObservationStore? ShadowObservationStore { get; init; }
}

public sealed record RustSelectorProcessResult
{
    public bool Started { get; init; }

    public bool TimedOut { get; init; }

    public int ExitCode { get; init; } = -1;

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public long ElapsedMilliseconds { get; init; }

    public string? FailureReason { get; init; }

    public bool ProcessTreeTerminated { get; init; }
}

public interface IRustSelectorProcessRunner
{
    RustSelectorProcessResult Run(string executablePath, string inputJson, int timeoutMs);
}

public sealed class RustSelectorProcessRunner : IRustSelectorProcessRunner
{
    public RustSelectorProcessResult Run(string executablePath, string inputJson, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
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
            if (!process.Start())
            {
                return Failure("StartupFailure", stopwatch);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(inputJson);
            process.StandardInput.Close();
            if (!process.WaitForExit(Math.Clamp(timeoutMs, 100, 30_000)))
            {
                var processTreeTerminated = false;
                try
                {
                    process.Kill(entireProcessTree: true);
                    processTreeTerminated = true;
                }
                catch (InvalidOperationException)
                {
                    // The process may have exited between the timeout and kill request.
                }
                process.WaitForExit();
                Task.WaitAll([outputTask, errorTask], TimeSpan.FromSeconds(1));
                stopwatch.Stop();
                return new RustSelectorProcessResult
                {
                    Started = true,
                    TimedOut = true,
                    StandardOutput = Completed(outputTask),
                    StandardError = Completed(errorTask),
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    FailureReason = "Timeout",
                    ProcessTreeTerminated = processTreeTerminated,
                };
            }

            Task.WaitAll(outputTask, errorTask);
            stopwatch.Stop();
            return new RustSelectorProcessResult
            {
                Started = true,
                ExitCode = process.ExitCode,
                StandardOutput = outputTask.Result,
                StandardError = errorTask.Result,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Failure("StartupFailure", stopwatch);
        }
    }

    private static RustSelectorProcessResult Failure(string reason, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new RustSelectorProcessResult
        {
            FailureReason = reason,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    private static string Completed(Task<string> task) => task.IsCompletedSuccessfully ? task.Result : string.Empty;
}

public sealed record RustEvidenceSelectorAttempt
{
    public bool Success { get; init; }

    public CoverageEvidenceSelectionResult? Selection { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public string FailureReason { get; init; } = string.Empty;

    public string Diagnostic { get; init; } = string.Empty;

    public double SelectorElapsedMilliseconds { get; init; }

    public bool TimedOut { get; init; }

    public int? ExitCode { get; init; }

    public RustSelectorFailureCategory FailureCategory { get; init; }

    public bool ProcessTreeTerminated { get; init; }
}

public interface IRustEvidenceSelectorClient
{
    RustEvidenceSelectorAttempt TrySelect(
        CoverageEvidenceSelectionRequest request,
        RustEvidenceSelectorOptions options);
}

public sealed class RustEvidenceSelectorClient : IRustEvidenceSelectorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRustSelectorProcessRunner processRunner;

    public RustEvidenceSelectorClient(IRustSelectorProcessRunner? processRunner = null)
    {
        this.processRunner = processRunner ?? new RustSelectorProcessRunner();
    }

    public RustEvidenceSelectorAttempt TrySelect(
        CoverageEvidenceSelectionRequest request,
        RustEvidenceSelectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        string? executablePath;
        try
        {
            executablePath = ResolveExecutablePath(options.ExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failed("InvalidExecutablePath", category: RustSelectorFailureCategory.InvalidExecutablePath);
        }
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return Failed("ExecutableMissing", category: RustSelectorFailureCategory.ExecutableMissing);
        }

        RustSelectorProcessResult process;
        try
        {
            process = processRunner.Run(
                executablePath,
                JsonSerializer.Serialize(request, JsonOptions),
                options.TimeoutMs);
        }
        catch (Exception exception)
        {
            return Failed("ProcessRunnerException", diagnostic: exception.GetType().Name,
                category: RustSelectorFailureCategory.UnexpectedException);
        }

        if (process.TimedOut)
        {
            return Failed("Timeout", process.ElapsedMilliseconds, timedOut: true,
                exitCode: process.ExitCode, category: RustSelectorFailureCategory.Timeout,
                processTreeTerminated: process.ProcessTreeTerminated);
        }
        if (!process.Started)
        {
            return Failed(process.FailureReason ?? "StartupFailure", process.ElapsedMilliseconds,
                category: RustSelectorFailureCategory.StartFailure);
        }
        if (process.ExitCode != 0)
        {
            return Failed($"NonZeroExit:{process.ExitCode}", process.ElapsedMilliseconds, Sanitize(process.StandardError),
                exitCode: process.ExitCode, category: RustSelectorFailureCategory.NonZeroExit);
        }
        if (string.IsNullOrWhiteSpace(process.StandardOutput))
        {
            return Failed("EmptyOutput", process.ElapsedMilliseconds, exitCode: process.ExitCode,
                category: RustSelectorFailureCategory.EmptyOutput);
        }

        RustSelectorOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<RustSelectorOutput>(process.StandardOutput, JsonOptions);
        }
        catch (JsonException)
        {
            return Failed("MalformedJson", process.ElapsedMilliseconds, exitCode: process.ExitCode,
                category: RustSelectorFailureCategory.MalformedJson);
        }
        if (output is null)
        {
            return Failed("SchemaMismatch", process.ElapsedMilliseconds, exitCode: process.ExitCode,
                category: RustSelectorFailureCategory.SchemaMismatch);
        }

        var validation = ValidateAndMap(request, output);
        return validation.Selection is null
            ? Failed(validation.Error, process.ElapsedMilliseconds, exitCode: process.ExitCode,
                category: CategorizeValidationFailure(validation.Error))
            : new RustEvidenceSelectorAttempt
            {
                Success = true,
                Selection = validation.Selection,
                ElapsedMilliseconds = process.ElapsedMilliseconds,
                SelectorElapsedMilliseconds = Math.Max(0, output.SelectorElapsedMs),
                ExitCode = process.ExitCode,
            };
    }

    public static string? ResolveExecutablePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return NormalizeExecutablePath(configuredPath);
        }
        var environmentPath = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return NormalizeExecutablePath(environmentPath);
        }
        return Path.Combine(AppContext.BaseDirectory, "tools", "rag-selector", "rag-selector-rs.exe");
    }

    private static string NormalizeExecutablePath(string path)
    {
        if (path.IndexOf('\0') >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("Executable path contains invalid characters.", nameof(path));
        }
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded.IndexOf('\0') >= 0 || expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("Executable path contains invalid characters.", nameof(path));
        }
        return Path.GetFullPath(expanded);
    }

    private static (CoverageEvidenceSelectionResult? Selection, string Error) ValidateAndMap(
        CoverageEvidenceSelectionRequest request,
        RustSelectorOutput output)
    {
        if (output.SelectedEvidenceIds is null || output.RequiredCoverage is null ||
            output.SearchCoverage is null || output.SelectedCoverage is null || output.MissingCoverage is null ||
            output.Warnings is null || output.Statuses is null || output.Decisions is null ||
            string.IsNullOrWhiteSpace(output.SelectionMode))
        {
            return (null, "SchemaMismatch");
        }

        var candidateMap = request.Candidates
            .Where(static item => !string.IsNullOrWhiteSpace(item.CandidateId))
            .GroupBy(static item => item.CandidateId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        if (output.SelectedEvidenceIds.Distinct(StringComparer.Ordinal).Count() != output.SelectedEvidenceIds.Count)
        {
            return (null, "DuplicateSelectedId");
        }
        if (output.SelectedEvidenceIds.Any(id => !candidateMap.ContainsKey(id)))
        {
            return (null, "UnknownSelectedId");
        }

        var selectedIds = output.SelectedEvidenceIds.ToHashSet(StringComparer.Ordinal);
        if (request.Candidates.Any(item => item.IsManuallySelected && !selectedIds.Contains(item.CandidateId)))
        {
            return (null, "ManualSelectionMissing");
        }
        if (request.Candidates.Any(item => selectedIds.Contains(item.CandidateId) &&
            !item.IsManuallySelected && (item.ExplicitlyExcluded || item.TopicConflict || item.ProductMismatch)))
        {
            return (null, "ExcludedCandidateSelected");
        }

        var manualCount = request.Candidates
            .Where(static item => item.IsManuallySelected)
            .Select(static item => item.CandidateId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var maxItems = Math.Clamp(Math.Max(Math.Clamp(request.BaseMaxItems, 1, 5), request.ExpansionMaxItems), 1, 5);
        if (output.SelectedEvidenceIds.Count > Math.Max(manualCount, maxItems))
        {
            return (null, "MaxItemsExceeded");
        }

        var selected = output.SelectedEvidenceIds.Select(id => candidateMap[id]).ToList();
        var expectedCoverage = selected.SelectMany(static item => item.Coverage)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!expectedCoverage.SequenceEqual(output.SelectedCoverage, StringComparer.Ordinal))
        {
            return (null, "SelectedCoverageMismatch");
        }
        if (output.SelectedEvidenceCount != selected.Count)
        {
            return (null, "SelectedCountMismatch");
        }

        return (new CoverageEvidenceSelectionResult
        {
            Selected = selected,
            Decisions = output.Decisions,
            RequiredCoverage = output.RequiredCoverage,
            SearchCoverage = output.SearchCoverage,
            SelectedCoverage = output.SelectedCoverage,
            MissingCoverage = output.MissingCoverage,
            Statuses = output.Statuses,
            Warnings = output.Warnings,
            RedundantCandidatesSkipped = output.RedundantCandidatesSkipped,
            BudgetLimited = output.BudgetLimited,
            EstimatedChars = output.EstimatedChars,
            SelectionMode = output.SelectionMode,
        }, string.Empty);
    }

    private static RustEvidenceSelectorAttempt Failed(
        string reason,
        long elapsed = 0,
        string diagnostic = "",
        bool timedOut = false,
        int? exitCode = null,
        RustSelectorFailureCategory category = RustSelectorFailureCategory.UnexpectedException,
        bool processTreeTerminated = false) => new()
    {
        FailureReason = reason,
        ElapsedMilliseconds = elapsed,
        Diagnostic = diagnostic,
        TimedOut = timedOut,
        ExitCode = exitCode,
        FailureCategory = category,
        ProcessTreeTerminated = processTreeTerminated,
    };

    private static RustSelectorFailureCategory CategorizeValidationFailure(string reason) => reason switch
    {
        "SchemaMismatch" or "SelectedCoverageMismatch" or "SelectedCountMismatch" or "MaxItemsExceeded" =>
            RustSelectorFailureCategory.SchemaMismatch,
        "UnknownSelectedId" => RustSelectorFailureCategory.UnknownEvidenceId,
        "DuplicateSelectedId" => RustSelectorFailureCategory.DuplicateEvidenceId,
        "ManualSelectionMissing" or "ExcludedCandidateSelected" => RustSelectorFailureCategory.ManualSelectionViolation,
        _ => RustSelectorFailureCategory.ValidationFailure,
    };

    private static string Sanitize(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine[..300];
    }

    private sealed record RustSelectorOutput
    {
        public IReadOnlyList<string>? SelectedEvidenceIds { get; init; }
        public IReadOnlyList<string>? RequiredCoverage { get; init; }
        public IReadOnlyList<string>? SearchCoverage { get; init; }
        public IReadOnlyList<string>? SelectedCoverage { get; init; }
        public IReadOnlyList<string>? MissingCoverage { get; init; }
        public int SelectedEvidenceCount { get; init; }
        public int RedundantCandidatesSkipped { get; init; }
        public bool BudgetLimited { get; init; }
        public IReadOnlyList<string>? Warnings { get; init; }
        public IReadOnlyList<string>? Statuses { get; init; }
        public IReadOnlyList<CoverageEvidenceSelectionDecision>? Decisions { get; init; }
        public int EstimatedChars { get; init; }
        public string? SelectionMode { get; init; }
        public double SelectorElapsedMs { get; init; }
    }
}
