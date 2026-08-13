using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Evidence;

public enum RustSelectorFailureCategory
{
    None,
    ExecutableMissing,
    InvalidExecutablePath,
    StartFailure,
    Timeout,
    NonZeroExit,
    EmptyOutput,
    MalformedJson,
    SchemaMismatch,
    UnknownEvidenceId,
    DuplicateEvidenceId,
    ManualSelectionViolation,
    ValidationFailure,
    UnexpectedException,
}

public enum RustSelectorParityStatus
{
    Passed,
    Mismatch,
    Fallback,
}

public enum RustAdoptionReadiness
{
    NotEnoughData,
    Ready,
    NeedsInvestigation,
    Blocked,
}

public sealed record RustSelectorReadinessPolicy
{
    public int MinimumProductionRuns { get; init; } = 50;

    public int MaxStoredRecords { get; init; } = 500;

    public int RetentionDays { get; init; } = 30;

    public double MaximumFallbackRate { get; init; } = 0.01;

    public static RustSelectorReadinessPolicy Create(int minimumProductionRuns, int maxStoredRecords) => new()
    {
        MinimumProductionRuns = Math.Clamp(minimumProductionRuns, 10, 10_000),
        MaxStoredRecords = Math.Clamp(maxStoredRecords, 50, 10_000),
    };
}

public sealed record RustSelectorShadowObservation
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string Mode { get; init; } = "Shadow";

    public string RankingMode { get; init; } = "CoverageAware";

    public bool IsSynthetic { get; init; }

    public int RequiredCoverageCount { get; init; }

    public int CandidateCount { get; init; }

    public int CSharpSelectedCount { get; init; }

    public int RustSelectedCount { get; init; }

    public bool OrderedMatch { get; init; }

    public bool SetMatch { get; init; }

    public bool CoverageMatch { get; init; }

    public bool MissingCoverageMatch { get; init; }

    public bool BudgetMatch { get; init; }

    public double CSharpElapsedMilliseconds { get; init; }

    public double RustProcessElapsedMilliseconds { get; init; }

    public double RustSelectorElapsedMilliseconds { get; init; }

    public bool FallbackOccurred { get; init; }

    public RustSelectorFailureCategory FallbackCategory { get; init; }

    public bool TimedOut { get; init; }

    public int? RustExitCode { get; init; }

    public RustSelectorParityStatus Parity { get; init; }

    public IReadOnlyList<string> CSharpSelectedIdHashes { get; init; } = [];

    public IReadOnlyList<string> RustSelectedIdHashes { get; init; } = [];
}

public sealed record RustSelectorShadowStatistics
{
    public int TotalRuns { get; init; }

    public int ProductionRuns { get; init; }

    public int SyntheticRuns { get; init; }

    public int ExactOrderMatchCount { get; init; }

    public double ExactOrderMatchRate { get; init; }

    public double SetMatchRate { get; init; }

    public double CoverageMatchRate { get; init; }

    public double MissingCoverageMatchRate { get; init; }

    public double BudgetMatchRate { get; init; }

    public int FallbackCount { get; init; }

    public double FallbackRate { get; init; }

    public int TimeoutCount { get; init; }

    public int InvalidOutputCount { get; init; }

    public int MalformedOutputCount { get; init; }

    public int UnknownEvidenceIdCount { get; init; }

    public IReadOnlyDictionary<string, int> FallbackCategories { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public double RustMedianElapsedMilliseconds { get; init; }

    public double RustP95ElapsedMilliseconds { get; init; }

    public double RustSelectorMedianElapsedMilliseconds { get; init; }

    public double CSharpMedianElapsedMilliseconds { get; init; }

    public double CSharpP95ElapsedMilliseconds { get; init; }

    public double EstimatedProcessOverheadMilliseconds { get; init; }

    public int ConsecutiveMismatchCount { get; init; }

    public RustAdoptionReadiness Readiness { get; init; }
}

public interface IRustSelectorShadowObservationStore
{
    RustSelectorShadowStatistics Record(
        RustSelectorShadowObservation observation,
        RustSelectorReadinessPolicy policy);

    RustSelectorShadowStatistics Snapshot(RustSelectorReadinessPolicy policy);
}

public sealed class RustSelectorShadowObservationStore : IRustSelectorShadowObservationStore
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object memoryLock = new();
    private readonly List<RustSelectorShadowObservation> memoryRecords = [];
    private readonly string? filePath;

    public RustSelectorShadowObservationStore(string? filePath = null)
    {
        this.filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public RustSelectorShadowStatistics Record(
        RustSelectorShadowObservation observation,
        RustSelectorReadinessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(policy);
        return Access(records =>
        {
            records.Add(observation);
            Trim(records, policy, observation.Timestamp);
        }, policy);
    }

    public RustSelectorShadowStatistics Snapshot(RustSelectorReadinessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Access(records => Trim(records, policy, DateTimeOffset.UtcNow), policy);
    }

    private RustSelectorShadowStatistics Access(
        Action<List<RustSelectorShadowObservation>> update,
        RustSelectorReadinessPolicy policy)
    {
        if (filePath is null)
        {
            lock (memoryLock)
            {
                update(memoryRecords);
                return Calculate(memoryRecords, policy);
            }
        }

        var fileLock = FileLocks.GetOrAdd(filePath, static _ => new object());
        lock (fileLock)
        {
            var records = Load(filePath);
            update(records);
            TrySave(filePath, records);
            return Calculate(records, policy);
        }
    }

    private static List<RustSelectorShadowObservation> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<RustSelectorShadowObservation>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void TrySave(string path, IReadOnlyList<RustSelectorShadowObservation> records)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions), new UTF8Encoding(false));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Shadow diagnostics must never interrupt the support workflow.
        }
    }

    private static void Trim(
        List<RustSelectorShadowObservation> records,
        RustSelectorReadinessPolicy policy,
        DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Clamp(policy.RetentionDays, 1, 365));
        records.RemoveAll(record => record.Timestamp < cutoff);
        records.Sort(static (left, right) => left.Timestamp.CompareTo(right.Timestamp));
        var excess = records.Count - Math.Clamp(policy.MaxStoredRecords, 50, 10_000);
        if (excess > 0)
        {
            records.RemoveRange(0, excess);
        }
    }

    public static RustSelectorShadowStatistics Calculate(
        IReadOnlyList<RustSelectorShadowObservation> records,
        RustSelectorReadinessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(policy);
        var production = records.Where(static record => !record.IsSynthetic).ToList();
        var measured = production.Count > 0 ? production : records.ToList();
        var total = measured.Count;
        var fallbackCount = measured.Count(static record => record.FallbackOccurred);
        var rustProcess = measured.Select(static record => record.RustProcessElapsedMilliseconds).Where(static value => value >= 0).ToList();
        var rustSelector = measured.Select(static record => record.RustSelectorElapsedMilliseconds).Where(static value => value >= 0).ToList();
        var csharp = measured.Select(static record => record.CSharpElapsedMilliseconds).Where(static value => value >= 0).ToList();
        var rustMedian = Percentile(rustProcess, 0.50);
        var rustSelectorMedian = Percentile(rustSelector, 0.50);
        return new RustSelectorShadowStatistics
        {
            TotalRuns = records.Count,
            ProductionRuns = production.Count,
            SyntheticRuns = records.Count - production.Count,
            ExactOrderMatchCount = measured.Count(static record => record.OrderedMatch),
            ExactOrderMatchRate = Rate(measured.Count(static record => record.OrderedMatch), total),
            SetMatchRate = Rate(measured.Count(static record => record.SetMatch), total),
            CoverageMatchRate = Rate(measured.Count(static record => record.CoverageMatch), total),
            MissingCoverageMatchRate = Rate(measured.Count(static record => record.MissingCoverageMatch), total),
            BudgetMatchRate = Rate(measured.Count(static record => record.BudgetMatch), total),
            FallbackCount = fallbackCount,
            FallbackRate = Rate(fallbackCount, total),
            TimeoutCount = measured.Count(static record => record.TimedOut),
            InvalidOutputCount = measured.Count(static record => IsInvalidOutput(record.FallbackCategory)),
            MalformedOutputCount = measured.Count(static record =>
                record.FallbackCategory == RustSelectorFailureCategory.MalformedJson),
            UnknownEvidenceIdCount = measured.Count(static record => record.FallbackCategory == RustSelectorFailureCategory.UnknownEvidenceId),
            FallbackCategories = measured
                .Where(static record => record.FallbackOccurred)
                .GroupBy(static record => record.FallbackCategory.ToString(), StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
            RustMedianElapsedMilliseconds = rustMedian,
            RustP95ElapsedMilliseconds = Percentile(rustProcess, 0.95),
            RustSelectorMedianElapsedMilliseconds = rustSelectorMedian,
            CSharpMedianElapsedMilliseconds = Percentile(csharp, 0.50),
            CSharpP95ElapsedMilliseconds = Percentile(csharp, 0.95),
            EstimatedProcessOverheadMilliseconds = Math.Max(0, rustMedian - rustSelectorMedian),
            ConsecutiveMismatchCount = ConsecutiveMismatches(production),
            Readiness = EvaluateReadiness(production, policy),
        };
    }

    private static RustAdoptionReadiness EvaluateReadiness(
        IReadOnlyList<RustSelectorShadowObservation> production,
        RustSelectorReadinessPolicy policy)
    {
        if (production.Count < Math.Clamp(policy.MinimumProductionRuns, 10, 10_000))
        {
            return RustAdoptionReadiness.NotEnoughData;
        }
        if (production.Any(static record => record.TimedOut || IsInvalidOutput(record.FallbackCategory) ||
            record.FallbackCategory == RustSelectorFailureCategory.UnknownEvidenceId))
        {
            return RustAdoptionReadiness.Blocked;
        }
        if (production.Any(static record => !record.OrderedMatch || !record.SetMatch ||
            !record.CoverageMatch || !record.MissingCoverageMatch || !record.BudgetMatch) ||
            Rate(production.Count(static record => record.FallbackOccurred), production.Count) > policy.MaximumFallbackRate)
        {
            return RustAdoptionReadiness.NeedsInvestigation;
        }
        return RustAdoptionReadiness.Ready;
    }

    private static bool IsInvalidOutput(RustSelectorFailureCategory category) => category is
        RustSelectorFailureCategory.EmptyOutput or
        RustSelectorFailureCategory.MalformedJson or
        RustSelectorFailureCategory.SchemaMismatch or
        RustSelectorFailureCategory.DuplicateEvidenceId or
        RustSelectorFailureCategory.ManualSelectionViolation or
        RustSelectorFailureCategory.ValidationFailure;

    private static int ConsecutiveMismatches(IReadOnlyList<RustSelectorShadowObservation> records)
    {
        var count = 0;
        for (var index = records.Count - 1; index >= 0; index--)
        {
            if (records[index].Parity == RustSelectorParityStatus.Passed)
            {
                break;
            }
            count++;
        }
        return count;
    }

    private static double Rate(int count, int total) => total == 0 ? 0 : count / (double)total;

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToList();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Count) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }
}

public static class RustSelectorPrivacy
{
    public static string HashEvidenceId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
