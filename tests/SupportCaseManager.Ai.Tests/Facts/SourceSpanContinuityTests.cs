using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Tests.Facts;

public sealed class SourceSpanContinuityTests
{
    [Fact]
    public void GenericScanAndConfigurationDoNotReplaceNamedSubject()
    {
        var source = new SearchSource { SourceId = "official:release", SourceType = "OfficialDoc", Url = "https://example.test/release",
            Text = "Before migration, verify the same configuration before the first scan build." };
        Assert.Empty(SelectedOfficialFactProjector.Project("DATA_BRIDGEのスキャン情報送信と設定を確認します。", [source]));
    }
    [Fact]
    public void ServerNameAloneDoesNotPromoteUnrelatedLicenseFailureInstructions()
    {
        var source = new SearchSource { SourceId = "manual:other", SourceType = "Manual",
            Text = "Validateサーバーへのアップロード時にライセンスエラーなら設定を確認する必要があります。" };
        Assert.Empty(SelectedOfficialFactProjector.Project("Windows 11 Pro 24H2でValidateのセットアップ手順と入手先を確認します。", [source]));
    }
    [Fact]
    public void ChoosesRelevantRecordedSourceBeyondTopTwoWithoutChangingOriginalText()
    {
        var irrelevant = new SearchSource { SourceId = "manual:other", SourceType = "Manual", Text = "Unrelated backup configuration." };
        var direct = new SearchSource { SourceId = "manual:command", SourceType = "Manual",
            Text = "Version 2025.2.\nThe widget_report command supports the detailed report before the first execution.",
            ContentHash = "original-document-hash" };
        var sources = Enumerable.Range(0, 8).Select(i => irrelevant with { SourceId = "s" + i }).Append(direct);
        var selected = SelectedOfficialFactProjector.SelectRelevantEvidence("widget_reportのレポート対応を確認します。", sources, 2);
        Assert.Same(direct, selected[0]);
        Assert.Equal("original-document-hash", selected[0].ContentHash);
        var fact = Assert.Single(SelectedOfficialFactProjector.Project("widget_reportのレポート対応を確認します。", [direct]));
        Assert.Equal("Manual", fact.SourceType);
        Assert.Equal("Manual", fact.AuthorityLevel);
        Assert.Contains("公式性を断定せず", fact.Statement);
        Assert.Contains("before the first execution", fact.Value);
        Assert.Contains(fact.Value, direct.Text);
        Assert.True(GroundedAnswerPromptBuilder.CitationMatchesSubject("widget_report", fact.Value, direct,
            new FactResolutionResult { ResolvedFacts = [fact] }));
    }

    [Fact]
    public void ContiguousConditionKeepsDecimalVersionAndDoesNotCombineSeparateRecords()
    {
        const string text = "If version 9.7.4 is used before the first build,\nverify the widget_report configuration.\nOther topic.";
        Assert.Contains(SelectedOfficialFactProjector.VerbatimSpans(text), span =>
            span.Contains("9.7.4") && span.Contains("before the first build") && span.Contains("verify the widget_report"));
        Assert.All(SelectedOfficialFactProjector.VerbatimSpans(text), span => Assert.Contains(span, text));
        Assert.Empty(SelectedOfficialFactProjector.Project("widget_report確認", [new SearchSource
            { SourceId = "case", SourceType = "CurrentCase", Text = text }]));
    }
}
