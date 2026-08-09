using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Tests.Ranking;

public sealed class TopicEntityAnalyzerTests
{
    private static readonly TopicEntityCatalog Catalog = new()
    {
        Products =
        [
            new TopicAliasDefinition
            {
                CanonicalName = "Perforce QAC",
                Aliases = ["Helix QAC", "QAC"],
            },
        ],
        Components =
        [
            new TopicAliasDefinition
            {
                CanonicalName = "Validate",
                Aliases = ["Perforce Validate"],
            },
        ],
        Features =
        [
            new TopicAliasDefinition { CanonicalName = "Stream", Aliases = ["ストリーム"] },
            new TopicAliasDefinition { CanonicalName = "License", Aliases = ["ライセンス"] },
            new TopicAliasDefinition { CanonicalName = "IDE Plugin", Aliases = ["IDEプラグイン", "IDE Plugin"] },
            new TopicAliasDefinition { CanonicalName = "Build upload", Aliases = ["validate build"] },
        ],
        Objects =
        [
            new TopicAliasDefinition { CanonicalName = "Analysis result", Aliases = ["解析結果"] },
        ],
        Entities =
        [
            new TopicEntityAliasDefinition
            {
                Kind = TopicEntityKind.Command,
                CanonicalValue = "qacli validate build",
                Aliases = ["validate build"],
            },
        ],
    };

    [Fact]
    public void Extract_FindsTopicOperationAndIntentFromExplicitText()
    {
        var profile = TopicEntityAnalyzer.Extract(
            "Perforce QACのValidateのストリーム機能について、機能概要と設定方法を教えてください。",
            Catalog);

        Assert.Equal(["Perforce QAC"], profile.Products);
        Assert.Equal(["Validate"], profile.Components);
        Assert.Equal(["Stream"], profile.Features);
        Assert.Contains("Configuration", profile.Operations);
        Assert.Contains("Overview", profile.Intents);
        Assert.Contains("HowTo", profile.Intents);
    }

    [Fact]
    public void Extract_FindsCommandUploadObjectAndExactEntities()
    {
        var profile = TopicEntityAnalyzer.Extract(
            "QACで解析結果をValidateへアップロードするには qacli validate build --project Demo を実行します。",
            Catalog);

        Assert.Equal(["Perforce QAC"], profile.Products);
        Assert.Equal(["Validate"], profile.Components);
        Assert.Equal(["Build upload"], profile.Features);
        Assert.Equal(["Analysis result"], profile.Objects);
        Assert.Contains("Upload", profile.Operations);
        Assert.Contains(profile.Entities, entity =>
            entity.Kind == TopicEntityKind.Command && entity.NormalizedValue == "validate build");
        Assert.Contains(profile.Entities, entity =>
            entity.Kind == TopicEntityKind.Option && entity.Value == "--project");
    }

    [Fact]
    public void Extract_DoesNotInventUnspecifiedTopicFields()
    {
        var profile = TopicEntityAnalyzer.Extract("設定方法を教えてください。", Catalog);

        Assert.Empty(profile.Products);
        Assert.Empty(profile.Components);
        Assert.Empty(profile.Features);
        Assert.Empty(profile.Objects);
        Assert.Contains("Configuration", profile.Operations);
    }

    [Fact]
    public void Extract_NormalizesCaseWidthAndCommandPrefix()
    {
        var profile = TopicEntityAnalyzer.Extract(
            "ＶＡＬＩＤＡＴＥ STREAM and validate build",
            Catalog);

        Assert.Equal(["Validate"], profile.Components);
        Assert.Equal(["Stream", "Build upload"], profile.Features);
        Assert.Contains(profile.Entities, entity =>
            entity.Kind == TopicEntityKind.Command && entity.NormalizedValue == "validate build");
    }

    [Fact]
    public void Extract_FindsSupportedEntityKindsWithoutTreatingAcronymAsSetting()
    {
        var profile = TopicEntityAnalyzer.Extract(
            "QAC 2025.4 on Windows uses Validate API, STREAM_MODE=enabled, VAL-1234, config.json and license server.",
            Catalog);

        Assert.Contains(profile.Entities, entity => entity.Kind == TopicEntityKind.Api && entity.Value == "Validate API");
        Assert.Contains(profile.Entities, entity => entity.Kind == TopicEntityKind.Setting && entity.Value == "STREAM_MODE=enabled");
        Assert.Contains(profile.Entities, entity => entity.Kind == TopicEntityKind.ErrorCode && entity.Value == "VAL-1234");
        Assert.Contains(profile.Entities, entity => entity.Kind == TopicEntityKind.File && entity.Value == "config.json");
        Assert.Contains(profile.Entities, entity => entity.Kind == TopicEntityKind.Version && entity.Value == "2025.4");
        Assert.Contains(profile.Entities, entity => entity.Kind == TopicEntityKind.OperatingSystem && entity.Value == "Windows");
        Assert.Contains(profile.Entities, entity => entity.Kind == TopicEntityKind.ServerType && entity.Value == "License Server");
        Assert.DoesNotContain(profile.Entities, entity => entity.Kind == TopicEntityKind.Setting && entity.Value == "QAC");
    }

    [Fact]
    public void Compare_DetectsStreamVersusLicenseConflict()
    {
        var query = TopicEntityAnalyzer.Extract("Validate Streamの設定方法", Catalog);
        var evidence = TopicEntityAnalyzer.Extract("Validate License server configuration", Catalog);

        var assessment = TopicEntityAnalyzer.Compare(query, evidence);

        Assert.True(assessment.TopicConflict);
        Assert.True(assessment.NoTopicMatch);
        Assert.Contains("Feature", assessment.ConflictKinds);
        Assert.Empty(assessment.MatchedFeatures);
    }

    [Fact]
    public void Compare_ReportsProductAndTopicMatchesForSameTopic()
    {
        var query = TopicEntityAnalyzer.Extract("QAC Validate Stream setup", Catalog);
        var evidence = TopicEntityAnalyzer.Extract("Perforce QAC Validate Stream configuration", Catalog);

        var assessment = TopicEntityAnalyzer.Compare(query, evidence);

        Assert.False(assessment.TopicConflict);
        Assert.True(assessment.HasTopicMatch);
        Assert.Equal(["Perforce QAC"], assessment.MatchedProducts);
        Assert.Equal(["Validate"], assessment.MatchedComponents);
        Assert.Equal(["Stream"], assessment.MatchedFeatures);
    }

    [Fact]
    public void Compare_DetectsStreamVersusIdePluginConflict()
    {
        var query = TopicEntityAnalyzer.Extract("Validate Stream overview", Catalog);
        var evidence = TopicEntityAnalyzer.Extract("Validate IDE Plugin setup", Catalog);

        var assessment = TopicEntityAnalyzer.Compare(query, evidence);

        Assert.True(assessment.TopicConflict);
        Assert.Contains("Feature", assessment.ConflictKinds);
    }

    [Fact]
    public void Compare_DoesNotReportConflictWhenEvidenceTopicIsUnknown()
    {
        var query = TopicEntityAnalyzer.Extract("Validate Stream overview", Catalog);
        var evidence = TopicEntityAnalyzer.Extract("General configuration guidance", Catalog);

        var assessment = TopicEntityAnalyzer.Compare(query, evidence);

        Assert.False(assessment.TopicConflict);
        Assert.True(assessment.NoTopicMatch);
    }

    [Fact]
    public void Compare_MatchesEquivalentCommandEntities()
    {
        var query = TopicEntityAnalyzer.Extract("qacli validate buildの方法", Catalog);
        var evidence = TopicEntityAnalyzer.Extract("validate build command reference", Catalog);

        var assessment = TopicEntityAnalyzer.Compare(query, evidence);

        Assert.Contains(assessment.MatchedEntities, entity =>
            entity.Kind == TopicEntityKind.Command && entity.NormalizedValue == "validate build");
    }
}
