using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Tests.Facts;

public sealed class SelectedOfficialFactProjectorTests
{
    [Fact]
    public void Project_UsesConditionalCheckerInstructionWithoutInferringDefaultValue()
    {
        var source = new SearchSource
        {
            SourceId = "official:release",
            SourceType = "OfficialDoc",
            Url = "https://help.klocwork.com/2026.2/en-us/concepts/releasenotes.htm",
            Text = "Release notes. Disabled checkers If you chose to migrate your projects_root directory, verify that you have the same checker configuration as the previous release before your first integration build analysis.",
        };

        var fact = Assert.Single(SelectedOfficialFactProjector.Project(
            "2026.2へのバージョンアップ時、チェッカー設定の既定enabledに変更はありますか。", [source]));

        Assert.Equal("OfficialDoc", fact.SourceType);
        Assert.Equal("Confirmed", fact.Status);
        Assert.Equal(source.SourceId, fact.EvidenceId);
        Assert.Contains("migrate your projects_root", fact.Value);
        Assert.Contains("条件:", fact.Statement);
        Assert.Contains("必要な対応: verify", fact.Statement);
        Assert.Contains("見出し『Disabled checkers』が存在", fact.Statement);
        Assert.DoesNotContain("enabled", fact.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_IgnoresUnrelatedOrNonOfficialText()
    {
        var unrelated = new SearchSource
        {
            SourceId = "official:other",
            SourceType = "OfficialDoc",
            Url = "https://help.klocwork.com/2026.2/en-us/concepts/releasenotes.htm",
            Text = "Release notes. Verify your license before the upgrade begins.",
        };
        var caseNote = unrelated with { SourceType = "PastCaseNote" };

        Assert.Empty(SelectedOfficialFactProjector.Project(
            "チェッカー設定の既定enabledに変更はありますか。", [unrelated, caseNote]));
    }

    [Fact]
    public void HasDirectTopicOverlap_DropsDifferentSymptomWhileKeepingDirectEvidence()
    {
        const string inquiry = "言語設定が英語に戻り、同期設定も消失します。";
        var unrelated = new SearchSource { Title = "Manual", Text = "Windows path length configuration." };
        var direct = new SearchSource { Title = "Manual", Text = "GUI language and sync configuration." };

        Assert.False(SelectedOfficialFactProjector.HasDirectTopicOverlap(inquiry, unrelated));
        Assert.True(SelectedOfficialFactProjector.HasDirectTopicOverlap(inquiry, direct));
    }
}
