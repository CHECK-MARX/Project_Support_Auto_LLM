using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase18EvidenceSelectionIntegrationTests
{
    [Fact]
    public void FeatureOff_PreservesLegacySelection()
    {
        var items = new[]
        {
            Item("a", 0.9, "same"),
            Item("b", 0.8, "same"),
            Item("c", 0.7, "different"),
        };

        var legacy = SearchSourceSelectionBuilder.Build(items, 2, 0.3);
        var featureOff = SearchSourceSelectionBuilder.Build(items, 2, 0.3, questionAwareContext: new()
        {
            Enabled = false,
            UseCoverageAwareEvidenceSelection = false,
        });

        Assert.Equal(
            legacy.Sources.Select(static source => source.SourceId),
            featureOff.Sources.Select(static source => source.SourceId));
        Assert.Equal(legacy.Warning, featureOff.Warning);
        Assert.Empty(featureOff.SelectionMode);
    }

    [Fact]
    public void FeatureOn_UsesLowerRankedEvidenceToCompleteCoverageAndSkipsDuplicate()
    {
        var items = new[]
        {
            Item("upload-a", 0.90, "Perforce QAC Validate qacli validate build --build-name BUILD --project PROJECT upload option"),
            Item("upload-duplicate", 0.89, "Perforce QAC Validate qacli validate build --build-name BUILD --project PROJECT upload option"),
            Item("auth", 0.75, "Perforce QAC Validate qacli auth token, validate connect server URL, project association"),
            Item("incremental", 0.70, "Perforce QAC Validate qacli validate ibuild incremental build"),
            Item("verify", 0.68, "Perforce QAC Validate portal verification, upload failed error log troubleshooting"),
        };

        var result = SearchSourceSelectionBuilder.Build(items, 3, 0.10, questionAwareContext: Context(5));

        Assert.DoesNotContain(result.Sources, static source => source.SourceId == "upload-duplicate");
        Assert.Contains(result.Sources, static source => source.SourceId == "auth");
        Assert.Contains(result.Sources, static source => source.SourceId == "incremental");
        Assert.Contains(result.Sources, static source => source.SourceId == "verify");
        Assert.Equal("CoverageAware", result.SelectionMode);
        Assert.Equal("Phase18CoverageAware", result.RankingMode);
        Assert.True(result.Sources.Count is >= 3 and <= 5);
    }

    [Fact]
    public void FeatureOn_RetainsManualSelectionsAboveConfiguredLimitAndWarns()
    {
        var items = Enumerable.Range(1, 4)
            .Select(index => Item($"manual-{index}", 0.1, $"Perforce QAC Validate qacli validate build --option-{index}"))
            .ToList();
        foreach (var item in items)
        {
            item.IsSelected = false;
            item.IsSelected = true;
        }

        var result = SearchSourceSelectionBuilder.Build(items, 3, 0.8, questionAwareContext: Context(3));

        Assert.Equal(4, result.Sources.Count);
        Assert.Contains("ManualSelectionExceedsLimit", result.Warning);
    }

    [Fact]
    public void FeatureOn_DoesNotReSelectManuallyExcludedEvidence()
    {
        var excluded = Item("license", 0.99, "Validate Stream license configuration verification");
        excluded.IsSelected = false;
        var relevant = Item("stream", 0.65, "Validate Stream overview purpose create configuration QAC association verification");

        var result = SearchSourceSelectionBuilder.Build(
            [excluded, relevant],
            3,
            0.10,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = "Validate Streamの概要、目的、作成、設定、QAC関連付け、確認方法。ライセンスは対象外。",
                ProductName = "HelixQAC",
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 5,
                MaxPromptChars = 6000,
            });

        Assert.DoesNotContain(result.Sources, static source => source.SourceId == "license");
        Assert.Contains(result.Sources, static source => source.SourceId == "stream");
    }

    private static QuestionAwareEvidenceSelectionContext Context(int maxItems) => new()
    {
        Enabled = true,
        InquiryText = "QAC解析結果をCLIからValidateへアップロードするため、auth、接続、プロジェクト関連付け、build-name、incremental build、確認方法、エラー対処を知りたい。",
        ProductName = "HelixQAC",
        UsePhase175QualityControls = true,
        UseCoverageAwareEvidenceSelection = true,
        CoverageAwareMaxEvidenceItems = maxItems,
        MaxPromptChars = 12000,
    };

    private static SearchSourceViewModel Item(string id, double score, string text) => new(
        new SearchSource
        {
            SourceId = id,
            SourceType = "Manual",
            ProductName = "HelixQAC",
            Title = id,
            Text = text,
            FilePath = $@"C:\manuals\{id}.txt",
            DocumentId = id,
            SectionTitle = id,
            Score = score,
        },
        isSelected: true);
}
