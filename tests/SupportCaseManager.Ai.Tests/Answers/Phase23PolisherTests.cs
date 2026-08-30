using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Prompts;
using Xunit;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase23PolisherTests
{
    [Fact]
    public void PolisherPrompt_ForbidsTechnicalAdditions()
    {
        var prompt = PolisherPromptBuilder.Build("qacli validate build --qaf-project .");

        Assert.Contains("文章校正", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("新しい技術情報", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("qacli validate build --qaf-project .", prompt.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRejectsChangedCommandAndAcceptsPreservedCommand()
    {
        const string deterministic = "qacli validate build --qaf-project . を実行します。Version 2026.2。";

        Assert.True(PolishedAnswerValidator.PreservesProtectedValues(
            deterministic,
            "qacli validate build --qaf-project . を実行してください。"));
        Assert.False(PolishedAnswerValidator.PreservesProtectedValues(
            deterministic,
            "qacli validate upload --qaf-project . を実行してください。"));
    }

    [Fact]
    public void DeterministicComposerUsesOnlyAvailableReferenceMetadata()
    {
        var answer = DeterministicAnswerComposer.ComposeHowTo(
        [
            new EvidenceItem
            {
                SourceId = "manual-1",
                SourceType = "Manual",
                Title = "QAC Manual",
                DocumentTitle = "Perforce-QAC-Manual",
                PageNumber = 14,
                SectionTitle = "Project analysis",
                Excerpt = "qacli analyze -P <project-directory>",
            },
        ]);

        Assert.Contains("Perforce-QAC-Manual Page 14 『Project analysis』", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Page 15", answer, StringComparison.Ordinal);
    }
}
