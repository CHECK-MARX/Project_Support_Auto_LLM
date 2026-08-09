using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Tests.Ranking;

public sealed class NegationAwareTopicAnalyzerTests
{
    private static readonly TopicEntityCatalog Catalog = SupportTopicCatalog.Create("HelixQAC");

    [Fact]
    public void Analyze_LicenseInsteadOfStream_MakesStreamPrimaryAndLicenseExcluded()
    {
        var result = NegationAwareTopicAnalyzer.Analyze("LicenseではなくStreamについて教えてください。", Catalog);

        Assert.Contains("Stream", result.PrimaryProfile.Features);
        Assert.Contains("License", result.ExcludedProfile.Features);
        Assert.DoesNotContain("License", result.PrimaryProfile.Features);
    }

    [Fact]
    public void Analyze_LicenseServerAndIdePluginInsteadOfStream_ExcludesBoth()
    {
        var result = NegationAwareTopicAnalyzer.Analyze(
            "License ServerやIDE PluginではなくValidateのStream機能について確認したい。", Catalog);

        Assert.Contains("Validate", result.PrimaryProfile.Products);
        Assert.Contains("Stream", result.PrimaryProfile.Features);
        Assert.Contains("License", result.ExcludedProfile.Features);
        Assert.Contains("IDE Plugin", result.ExcludedProfile.Features);
        Assert.Contains(result.ExcludedProfile.Entities, item =>
            item.Kind == TopicEntityKind.ServerType && item.Value == "License Server");
    }

    [Fact]
    public void Analyze_StreamInsteadOfLicenseServer_ReversesPrimaryAndExcluded()
    {
        var result = NegationAwareTopicAnalyzer.Analyze("StreamではなくLicense Serverを確認したい。", Catalog);

        Assert.Contains("Stream", result.ExcludedProfile.Features);
        Assert.Contains("License", result.PrimaryProfile.Features);
        Assert.Contains(result.PrimaryProfile.Entities, item => item.Value == "License Server");
    }

    [Fact]
    public void Analyze_NoNegation_MatchesPhase16Extraction()
    {
        const string inquiry = "Validate Streamの概要、作成、QACとの関連付け、設定後の確認方法";
        var expected = TopicEntityAnalyzer.Extract(inquiry, Catalog);
        var result = NegationAwareTopicAnalyzer.Analyze(inquiry, Catalog);

        Assert.Equal(expected.Products, result.PrimaryProfile.Products);
        Assert.Equal(expected.Components, result.PrimaryProfile.Components);
        Assert.Equal(expected.Features, result.PrimaryProfile.Features);
        Assert.Equal(expected.Operations, result.PrimaryProfile.Operations);
        Assert.Equal(expected.Objects, result.PrimaryProfile.Objects);
        Assert.Equal(expected.Intents, result.PrimaryProfile.Intents);
        Assert.Equal(
            expected.Entities.Select(static item => (item.Kind, item.NormalizedValue)),
            result.PrimaryProfile.Entities.Select(static item => (item.Kind, item.NormalizedValue)));
        Assert.Empty(result.ExcludedProfile.Features);
        Assert.Empty(result.ExcludedTextSegments);
    }

    [Fact]
    public void Analyze_UnknownTopic_DoesNotGuessKnownTopic()
    {
        var result = NegationAwareTopicAnalyzer.Analyze("未知製品Alphaではなく未知機能Betaのみ確認したい。", Catalog);

        Assert.Empty(result.PrimaryProfile.Products);
        Assert.Empty(result.PrimaryProfile.Features);
        Assert.Empty(result.ExcludedProfile.Products);
        Assert.Empty(result.ExcludedProfile.Features);
    }
}
