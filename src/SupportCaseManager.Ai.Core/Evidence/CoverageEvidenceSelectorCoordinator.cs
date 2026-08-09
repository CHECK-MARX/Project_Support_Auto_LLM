using System.Diagnostics;

namespace SupportCaseManager.Ai.Core.Evidence;

public sealed record CoverageEvidenceSelectorExecution
{
    public CoverageEvidenceSelectionResult Selection { get; init; } = new();

    public string Engine { get; init; } = "CSharp";

    public double CSharpElapsedMilliseconds { get; init; }

    public long RustElapsedMilliseconds { get; init; }

    public double RustSelectorElapsedMilliseconds { get; init; }

    public int? RustExitCode { get; init; }

    public string FallbackReason { get; init; } = string.Empty;

    public string ParityValidation { get; init; } = "not applicable";

    public RustSelectorShadowObservation? ShadowObservation { get; init; }

    public RustSelectorShadowStatistics? ShadowStatistics { get; init; }

    public RustEvidenceSelectorWorkerHealth? PersistentWorkerHealth { get; init; }
}

public static class CoverageEvidenceSelectorCoordinator
{
    public static CoverageEvidenceSelectorExecution Select(
        CoverageEvidenceSelectionRequest request,
        RustEvidenceSelectorOptions? options = null,
        IRustEvidenceSelectorClient? rustClient = null,
        IRustEvidenceSelectorWorkerClient? workerClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new RustEvidenceSelectorOptions();
        if (!options.UseRustEvidenceSelector && !options.EnableRustSelectorShadowMode)
        {
            var csharpOnly = TimedCSharp(request);
            return new CoverageEvidenceSelectorExecution
            {
                Selection = csharpOnly.Selection,
                CSharpElapsedMilliseconds = csharpOnly.ElapsedMilliseconds,
            };
        }

        rustClient ??= new RustEvidenceSelectorClient();
        if (options.EnableRustSelectorShadowMode)
        {
            var csharp = TimedCSharp(request);
            var (attempt, _, workerFallbackReason) = TryRust(
                request, options, rustClient, workerClient, cancellationToken);
            var observation = BuildObservation(request, options, csharp, attempt);
            return new CoverageEvidenceSelectorExecution
            {
                Selection = csharp.Selection,
                Engine = "CSharp",
                CSharpElapsedMilliseconds = csharp.ElapsedMilliseconds,
                RustElapsedMilliseconds = attempt.ElapsedMilliseconds,
                RustSelectorElapsedMilliseconds = attempt.SelectorElapsedMilliseconds,
                RustExitCode = attempt.ExitCode,
                FallbackReason = CombineReasons(workerFallbackReason, attempt.Success ? string.Empty : attempt.FailureReason),
                ParityValidation = FormatParity(observation),
                ShadowObservation = observation,
                ShadowStatistics = RecordObservation(observation, options),
                PersistentWorkerHealth = GetWorkerHealth(workerClient),
            };
        }

        var (rustAttempt, engine, workerFailure) = TryRust(
            request, options, rustClient, workerClient, cancellationToken);
        if (rustAttempt.Success)
        {
            return new CoverageEvidenceSelectorExecution
            {
                Selection = rustAttempt.Selection!,
                Engine = engine,
                RustElapsedMilliseconds = rustAttempt.ElapsedMilliseconds,
                RustSelectorElapsedMilliseconds = rustAttempt.SelectorElapsedMilliseconds,
                RustExitCode = rustAttempt.ExitCode,
                FallbackReason = workerFailure,
                PersistentWorkerHealth = GetWorkerHealth(workerClient),
            };
        }

        var fallback = TimedCSharp(request);
        return new CoverageEvidenceSelectorExecution
        {
            Selection = fallback.Selection,
            Engine = "RustFallback",
            CSharpElapsedMilliseconds = fallback.ElapsedMilliseconds,
            RustElapsedMilliseconds = rustAttempt.ElapsedMilliseconds,
            RustSelectorElapsedMilliseconds = rustAttempt.SelectorElapsedMilliseconds,
            RustExitCode = rustAttempt.ExitCode,
            FallbackReason = CombineReasons(workerFailure, rustAttempt.FailureReason),
            PersistentWorkerHealth = GetWorkerHealth(workerClient),
        };
    }

    private static (RustEvidenceSelectorAttempt Attempt, string Engine, string WorkerFallbackReason) TryRust(
        CoverageEvidenceSelectionRequest request,
        RustEvidenceSelectorOptions options,
        IRustEvidenceSelectorClient rustClient,
        IRustEvidenceSelectorWorkerClient? workerClient,
        CancellationToken cancellationToken)
    {
        if (!options.UsePersistentRustEvidenceSelector)
        {
            return (rustClient.TrySelect(request, options), "Rust", string.Empty);
        }

        var workerAttempt = workerClient is null
            ? RustEvidenceSelectorClient.CreateFailure(
                "WorkerClientUnavailable",
                category: RustSelectorFailureCategory.StartFailure)
            : workerClient.TrySelect(request, options, cancellationToken);
        if (workerAttempt.Success)
        {
            return (workerAttempt, "PersistentRust", string.Empty);
        }

        workerClient?.RecordFallback();
        var singleShotAttempt = rustClient.TrySelect(request, options);
        return (singleShotAttempt, "Rust", $"Worker:{workerAttempt.FailureReason}");
    }

    private static RustEvidenceSelectorWorkerHealth? GetWorkerHealth(
        IRustEvidenceSelectorWorkerClient? workerClient)
    {
        try
        {
            return workerClient?.GetHealth();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static string CombineReasons(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }
        return string.IsNullOrWhiteSpace(second) ? first : $"{first}; SingleShot:{second}";
    }

    private static (CoverageEvidenceSelectionResult Selection, double ElapsedMilliseconds) TimedCSharp(
        CoverageEvidenceSelectionRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var selection = CoverageAwareEvidenceSelector.Select(request);
        stopwatch.Stop();
        return (selection, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static RustSelectorShadowObservation BuildObservation(
        CoverageEvidenceSelectionRequest request,
        RustEvidenceSelectorOptions options,
        (CoverageEvidenceSelectionResult Selection, double ElapsedMilliseconds) csharp,
        RustEvidenceSelectorAttempt rust)
    {
        var csharpIds = csharp.Selection.Selected.Select(static item => item.CandidateId).ToList();
        var rustIds = rust.Selection?.Selected.Select(static item => item.CandidateId).ToList() ?? [];
        var orderedMatch = rust.Success && csharpIds.SequenceEqual(rustIds, StringComparer.Ordinal);
        var setMatch = rust.Success && csharpIds.ToHashSet(StringComparer.Ordinal).SetEquals(rustIds);
        var coverageMatch = rust.Success && csharp.Selection.SelectedCoverage
            .SequenceEqual(rust.Selection!.SelectedCoverage, StringComparer.Ordinal);
        var missingCoverageMatch = rust.Success && csharp.Selection.MissingCoverage
            .SequenceEqual(rust.Selection!.MissingCoverage, StringComparer.Ordinal);
        var budgetMatch = rust.Success && csharp.Selection.BudgetLimited == rust.Selection!.BudgetLimited;
        var parity = !rust.Success
            ? RustSelectorParityStatus.Fallback
            : orderedMatch && setMatch && coverageMatch && missingCoverageMatch && budgetMatch
                ? RustSelectorParityStatus.Passed
                : RustSelectorParityStatus.Mismatch;
        return new RustSelectorShadowObservation
        {
            RankingMode = string.IsNullOrWhiteSpace(options.RankingMode) ? "CoverageAware" : options.RankingMode,
            IsSynthetic = options.IsSyntheticObservation,
            RequiredCoverageCount = request.RequiredCoverage.Count,
            CandidateCount = request.Candidates.Count,
            CSharpSelectedCount = csharpIds.Count,
            RustSelectedCount = rustIds.Count,
            OrderedMatch = orderedMatch,
            SetMatch = setMatch,
            CoverageMatch = coverageMatch,
            MissingCoverageMatch = missingCoverageMatch,
            BudgetMatch = budgetMatch,
            CSharpElapsedMilliseconds = csharp.ElapsedMilliseconds,
            RustProcessElapsedMilliseconds = rust.ElapsedMilliseconds,
            RustSelectorElapsedMilliseconds = rust.SelectorElapsedMilliseconds,
            FallbackOccurred = !rust.Success,
            FallbackCategory = rust.Success ? RustSelectorFailureCategory.None : rust.FailureCategory,
            TimedOut = rust.TimedOut,
            RustExitCode = rust.ExitCode,
            Parity = parity,
            CSharpSelectedIdHashes = parity == RustSelectorParityStatus.Mismatch
                ? csharpIds.Select(RustSelectorPrivacy.HashEvidenceId).ToList()
                : [],
            RustSelectedIdHashes = parity == RustSelectorParityStatus.Mismatch
                ? rustIds.Select(RustSelectorPrivacy.HashEvidenceId).ToList()
                : [],
        };
    }

    private static RustSelectorShadowStatistics? RecordObservation(
        RustSelectorShadowObservation observation,
        RustEvidenceSelectorOptions options)
    {
        try
        {
            var policy = RustSelectorReadinessPolicy.Create(
                options.ShadowMinimumRunsForReadiness,
                options.ShadowMaxStoredRecords);
            var store = options.ShadowObservationStore ??
                new RustSelectorShadowObservationStore(options.ShadowObservationFilePath);
            return store.Record(observation, policy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string FormatParity(RustSelectorShadowObservation observation) => observation.Parity switch
    {
        RustSelectorParityStatus.Passed => "passed",
        RustSelectorParityStatus.Fallback => $"fallback; category={observation.FallbackCategory}",
        _ => $"failed; orderedMatch={observation.OrderedMatch.ToString().ToLowerInvariant()}; " +
            $"setMatch={observation.SetMatch.ToString().ToLowerInvariant()}; " +
            $"coverageMatch={observation.CoverageMatch.ToString().ToLowerInvariant()}; " +
            $"missingCoverageMatch={observation.MissingCoverageMatch.ToString().ToLowerInvariant()}; " +
            $"budgetMatch={observation.BudgetMatch.ToString().ToLowerInvariant()}",
    };
}
