using System.Diagnostics;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Evidence;

const int warmup = 100;
const int algorithmSamples = 10_000;
const int processSamples = 100;
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var fixturePath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(root, "tools", "rag-lab", "samples", "phase18_coverage_selection_cases.json");
var rustExecutable = args.Length > 1 ? Path.GetFullPath(args[1]) : string.Empty;
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var cases = JsonSerializer.Deserialize<List<FixtureCase>>(File.ReadAllText(fixturePath), jsonOptions) ?? [];
var request = ToRequest(cases.First());

for (var index = 0; index < warmup; index++)
{
    _ = CoverageAwareEvidenceSelector.Select(request);
}
var csharpTimings = Measure(algorithmSamples, () => CoverageAwareEvidenceSelector.Select(request));
Console.WriteLine(JsonSerializer.Serialize(Result("CSharpAlgorithm", warmup, csharpTimings, false), JsonSerializerOptions.Web));

if (File.Exists(rustExecutable))
{
    var client = new RustEvidenceSelectorClient();
    var options = new RustEvidenceSelectorOptions
    {
        UseRustEvidenceSelector = true,
        ExecutablePath = rustExecutable,
        TimeoutMs = 2000,
    };
    for (var index = 0; index < warmup; index++)
    {
        Ensure(client.TrySelect(request, options));
    }
    var processTimings = Measure(processSamples, () => Ensure(client.TrySelect(request, options)));
    Console.WriteLine(JsonSerializer.Serialize(Result("RustCliTotal", warmup, processTimings, true), JsonSerializerOptions.Web));
}

static List<double> Measure(int samples, Action action)
{
    var timings = new List<double>(samples);
    for (var index = 0; index < samples; index++)
    {
        var started = Stopwatch.GetTimestamp();
        action();
        timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    timings.Sort();
    return timings;
}

static object Result(string engine, int warmup, IReadOnlyList<double> timings, bool process) => new
{
    Engine = engine,
    Warmup = warmup,
    Samples = timings.Count,
    MedianMs = Percentile(timings, 0.50),
    P95Ms = Percentile(timings, 0.95),
    ProcessStartupIncluded = process,
};

static double Percentile(IReadOnlyList<double> values, double percentile) =>
    values[(int)Math.Ceiling((values.Count - 1) * percentile)];

static void Ensure(RustEvidenceSelectorAttempt attempt)
{
    if (!attempt.Success)
    {
        throw new InvalidOperationException($"Rust benchmark failed: {attempt.FailureReason}");
    }
}

static CoverageEvidenceSelectionRequest ToRequest(FixtureCase fixture) => new()
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

sealed record FixtureCase
{
    public IReadOnlyList<string> RequiredCoverage { get; init; } = [];
    public int BaseMaxItems { get; init; } = 3;
    public int ExpansionMaxItems { get; init; } = 5;
    public int CharacterBudget { get; init; } = 2000;
    public double MinimumQualityScore { get; init; } = 0.30;
    public IReadOnlyList<FixtureCandidate> Candidates { get; init; } = [];
}

sealed record FixtureCandidate
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
