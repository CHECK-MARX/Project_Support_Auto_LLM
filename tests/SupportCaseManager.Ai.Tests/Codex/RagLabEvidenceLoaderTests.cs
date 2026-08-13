using System.Text.Json;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class RagLabEvidenceLoaderTests
{
    [Fact]
    public async Task LoadAsync_Disabled_DoesNotReadFiles()
    {
        var result = await new RagLabEvidenceLoader().LoadAsync(new RagLabEvidenceLoadRequest());

        Assert.False(result.IsEnabled);
        Assert.False(result.HasEvidence);
        Assert.Equal("Disabled", result.FallbackReason);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task LoadAsync_ReadsNormalTopThreeAndPreservesFields()
    {
        using var files = new EvidenceFiles(4);

        var result = await LoadAsync(files, maxItems: 3);

        Assert.True(result.IsBaselineReady);
        Assert.Equal("人工問い合わせ", result.Query);
        Assert.Equal(3, result.Evidence.Count);
        var first = result.Evidence[0];
        Assert.Equal("SyntheticManual", first.SourceType);
        Assert.Equal("doc-1", first.DocumentId);
        Assert.Equal("SYN-0001", first.SupportId);
        Assert.Equal("Checkmarx", first.Product);
        Assert.Equal("SYNTHETIC-1.0", first.Version);
        Assert.Equal(0.9, first.Score);
        Assert.Equal("人工選定理由1", first.SelectionReason);
        Assert.Equal("人工根拠本文1", first.Text);
        Assert.True(first.ProductMatch);
        Assert.True(first.VersionMatch);
        Assert.Equal(["term-1"], first.KeywordMatches);
        Assert.False(first.PossiblyStale);
        Assert.False(first.PossibleConflict);
        Assert.Equal(["人工未確認項目1"], first.UnverifiedItems);
    }

    [Fact]
    public async Task LoadAsync_LimitsEvidenceToFive()
    {
        using var files = new EvidenceFiles(8);

        var result = await LoadAsync(files, maxItems: 20);

        Assert.Equal(5, result.Evidence.Count);
        Assert.Equal("doc-5", result.Evidence[^1].DocumentId);
    }

    [Fact]
    public async Task LoadAsync_EmptyEvidence_FallsBack()
    {
        using var files = new EvidenceFiles(0);

        var result = await LoadAsync(files);

        Assert.False(result.HasEvidence);
        Assert.Equal("EmptyEvidence", result.FallbackReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("根拠がありません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_FallsBackWithoutThrowing()
    {
        using var files = new EvidenceFiles(1);
        await File.WriteAllTextAsync(files.EvidencePath, "{ invalid");

        var result = await LoadAsync(files);

        Assert.False(result.HasEvidence);
        Assert.Equal("ReadFailed", result.FallbackReason);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task LoadAsync_BaselineNotReady_FallsBackBeforeEvidenceRead()
    {
        using var files = new EvidenceFiles(1, readinessStatus: "blocked");
        File.Delete(files.EvidencePath);

        var result = await LoadAsync(files);

        Assert.False(result.IsBaselineReady);
        Assert.Equal("BaselineNotReady", result.FallbackReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_MissingEvidenceFile_RepresentsGenerationFailureAsFallback()
    {
        using var files = new EvidenceFiles(1);
        File.Delete(files.EvidencePath);

        var result = await LoadAsync(files);

        Assert.False(result.HasEvidence);
        Assert.Equal("ReadFailed", result.FallbackReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("見つかりません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_ProductMismatch_IsExcluded()
    {
        using var files = new EvidenceFiles(1, product: "HelixQAC", productMatch: false);

        var result = await LoadAsync(files, expectedProduct: "Checkmarx");

        Assert.False(result.HasEvidence);
        Assert.Equal("NoApplicableEvidence", result.FallbackReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("製品", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_VersionMismatch_IsRetainedWithWarning()
    {
        using var files = new EvidenceFiles(
            1,
            version: "SYNTHETIC-1.0",
            versionMatch: false,
            evidenceWarnings: ["人工警告"]);

        var result = await LoadAsync(files, expectedVersion: "SYNTHETIC-2.0");

        var evidence = Assert.Single(result.Evidence);
        Assert.Contains("人工警告", evidence.Warnings);
        Assert.Contains(evidence.Warnings, warning => warning.Contains("バージョン", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_PreservesExistingWarningsWithoutDuplication()
    {
        const string versionWarning = "対象バージョンと根拠のバージョンが一致しません。";
        using var files = new EvidenceFiles(1, versionMatch: false, evidenceWarnings: [versionWarning, "鮮度警告"]);

        var result = await LoadAsync(files, expectedVersion: "SYNTHETIC-2.0");

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(2, evidence.Warnings.Count);
        Assert.Equal(versionWarning, evidence.Warnings[0]);
        Assert.Equal("鮮度警告", evidence.Warnings[1]);
    }

    private static Task<RagLabEvidenceLoadResult> LoadAsync(
        EvidenceFiles files,
        int maxItems = 3,
        string expectedProduct = "Checkmarx",
        string? expectedVersion = null)
    {
        return new RagLabEvidenceLoader().LoadAsync(new RagLabEvidenceLoadRequest
        {
            IsEnabled = true,
            EvidenceFilePath = files.EvidencePath,
            BaselineReadinessFilePath = files.ReadinessPath,
            MaxItems = maxItems,
            ExpectedProduct = expectedProduct,
            ExpectedVersion = expectedVersion,
        });
    }

    private sealed class EvidenceFiles : IDisposable
    {
        private readonly TempDirectory temp = new();

        public EvidenceFiles(
            int count,
            string readinessStatus = "ready",
            string product = "Checkmarx",
            string version = "SYNTHETIC-1.0",
            bool? productMatch = true,
            bool? versionMatch = true,
            IReadOnlyList<string>? evidenceWarnings = null)
        {
            ReadinessPath = Path.Combine(temp.Path, "baseline-readiness.json");
            EvidencePath = Path.Combine(temp.Path, "evidence.json");
            File.WriteAllText(ReadinessPath, JsonSerializer.Serialize(new { status = readinessStatus }));
            var selectedEvidence = Enumerable.Range(1, count).Select(index => new
            {
                sourceType = "SyntheticManual",
                documentId = $"doc-{index}",
                supportId = $"SYN-{index:0000}",
                product,
                version,
                score = 1.0 - index / 10.0,
                selectionReason = $"人工選定理由{index}",
                warnings = evidenceWarnings ?? [],
                text = $"人工根拠本文{index}",
                productMatch,
                versionMatch,
                keywordMatches = new[] { $"term-{index}" },
                possiblyStale = false,
                possibleConflict = false,
                unverifiedItems = new[] { $"人工未確認項目{index}" },
            });
            File.WriteAllText(EvidencePath, JsonSerializer.Serialize(new
            {
                query = "人工問い合わせ",
                selectedEvidence,
            }));
        }

        public string ReadinessPath { get; }
        public string EvidencePath { get; }
        public void Dispose() => temp.Dispose();
    }
}
