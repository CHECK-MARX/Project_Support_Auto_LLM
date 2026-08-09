using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;

namespace SupportCaseManager.Ai.Tests.Quality;

public sealed class AnswerQualityEvaluatorTests
{
    [Fact]
    public void Evaluate_MatchesSyntheticPhase17Expectations()
    {
        var fixture = LoadFixture();
        var falsePositives = 0;
        var falseNegatives = 0;

        foreach (var testCase in fixture.Cases)
        {
            var result = Evaluate(testCase, "phase16");

            Assert.Equal(testCase.ExpectedDecision, result.Decision);
            Assert.Equal(testCase.ExpectedUnsupportedClaimCount, result.UnsupportedClaimCount);

            falsePositives += Math.Max(
                0,
                result.UnsupportedClaimCount - testCase.ExpectedUnsupportedClaimCount);
            falseNegatives += Math.Max(
                0,
                testCase.ExpectedUnsupportedClaimCount - result.UnsupportedClaimCount);
        }

        Assert.Equal(0, falsePositives);
        Assert.Equal(0, falseNegatives);
    }

    [Fact]
    public void Evaluate_SeparatesTechnicalCorrectnessFromWritingQuality()
    {
        var testCase = LoadFixture().Cases.Single(item =>
            item.Id == "G-correct-technical-awkward-style");

        var result = Evaluate(testCase, "phase16");

        Assert.Equal(1, result.Grounding);
        Assert.Equal(1, result.TechnicalFidelity);
        Assert.True(result.CustomerReadiness < 0.65);
        Assert.Equal(AnswerQualityDecisions.NeedsReview, result.Decision);
    }

    [Fact]
    public void Evaluate_BlocksUnsupportedCommandAndOption()
    {
        var testCase = LoadFixture().Cases.Single(item =>
            item.Id == "D-unsupported-command");

        var result = Evaluate(testCase, "phase16");

        Assert.Equal(AnswerQualityDecisions.Blocked, result.Decision);
        Assert.Contains(result.UnsupportedTechnicalClaims, item => item.Kind == "Command");
        Assert.Contains(result.UnsupportedTechnicalClaims, item => item.Kind == "Option");
        Assert.Contains("MajorUnsupportedTechnicalClaim", result.BlockingReasons);
    }

    [Fact]
    public void Evaluate_BlocksUnsupportedVersion()
    {
        var result = AnswerQualityEvaluator.Evaluate(new AnswerQualityEvaluationInput
        {
            Question = "Validate Stream Version 2025.4の設定方法を教えてください。",
            Answer = "Validate StreamをVersion 9.9で設定してください。",
            ProductName = "HelixQAC",
            Evidence =
            [
                new AnswerQualityEvidence
                {
                    SourceId = "version-manual",
                    SourceType = "Manual",
                    Text = "Validate Stream Version 2025.4の設定方法です。",
                    Version = "2025.4",
                },
            ],
            Catalog = AnswerQualityEvaluator.CreateSupportCatalog("HelixQAC"),
        });

        Assert.Equal(AnswerQualityDecisions.Blocked, result.Decision);
        Assert.Contains(
            result.UnsupportedTechnicalClaims,
            item => item.Kind == "Version" && item.Value.Contains("9.9", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ReportsVersionConflictAndInternalLeakage()
    {
        var fixture = LoadFixture();

        var conflict = Evaluate(
            fixture.Cases.Single(item => item.Id == "E-version-conflict"),
            "phase16");
        var leakage = Evaluate(
            fixture.Cases.Single(item => item.Id == "H-internal-leakage"),
            "phase16");

        Assert.True(conflict.ConflictCount > 0);
        Assert.Equal(AnswerQualityDecisions.NeedsReview, conflict.Decision);
        Assert.True(leakage.InternalLeakageCount > 0);
        Assert.Equal(AnswerQualityDecisions.NeedsReview, leakage.Decision);
    }

    [Fact]
    public void TechnicalClaimExtractor_HandlesFileAdjacentToJapaneseText()
    {
        var claims = TechnicalClaimExtractor.Extract(
            "config.jsonを使用し、qacli validate build --project Demoを実行します。",
            AnswerQualityEvaluator.CreateSupportCatalog("HelixQAC"));

        Assert.Contains(claims, item => item.Kind == "File" && item.Value == "config.json");
        Assert.Contains(claims, item => item.Kind == "Command");
        Assert.Contains(claims, item => item.Kind == "Option");
    }

    [Fact]
    public void Result_IsJsonSerializable()
    {
        var testCase = LoadFixture().Cases.First();
        var result = Evaluate(testCase, "phase16");

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<AnswerQualityEvaluationResult>(json);

        Assert.NotNull(restored);
        Assert.Equal(result.Decision, restored.Decision);
        Assert.Equal(result.UnsupportedClaimCount, restored.UnsupportedClaimCount);
    }

    private static AnswerQualityEvaluationResult Evaluate(
        SyntheticCase testCase,
        string phase)
    {
        return AnswerQualityEvaluator.Evaluate(new AnswerQualityEvaluationInput
        {
            Question = testCase.Question,
            Answer = testCase.Answers[phase],
            ProductName = testCase.Product,
            Evidence = testCase.Evidence.Select(item => new AnswerQualityEvidence
            {
                SourceId = item.SourceId,
                SourceType = item.SourceType,
                Text = item.Text,
                ProductName = testCase.Product,
                Version = item.Version,
            }).ToList(),
            Catalog = AnswerQualityEvaluator.CreateSupportCatalog(testCase.Product),
        });
    }

    private static SyntheticFixture LoadFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "phase17_answer_quality_cases.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SyntheticFixture>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Phase 17 synthetic fixture is invalid.");
    }

    private sealed record SyntheticFixture
    {
        public IReadOnlyList<SyntheticCase> Cases { get; init; } = [];
    }

    private sealed record SyntheticCase
    {
        public string Id { get; init; } = string.Empty;
        public string Product { get; init; } = string.Empty;
        public string Question { get; init; } = string.Empty;
        public IReadOnlyList<string> EvidenceTopK { get; init; } = [];
        public IReadOnlyList<SyntheticEvidence> Evidence { get; init; } = [];
        public IReadOnlyDictionary<string, string> Answers { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public string ExpectedDecision { get; init; } = string.Empty;
        public int ExpectedUnsupportedClaimCount { get; init; }
    }

    private sealed record SyntheticEvidence
    {
        public string SourceId { get; init; } = string.Empty;
        public string SourceType { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public string? Version { get; init; }
    }
}
