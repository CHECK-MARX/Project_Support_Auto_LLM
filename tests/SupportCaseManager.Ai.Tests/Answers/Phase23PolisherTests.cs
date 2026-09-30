using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using Xunit;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase23PolisherTests
{
    [Fact]
    public void PolisherPrompt_RejectsOversizedContextWithoutTruncatingAnswer()
    {
        var error = Assert.Throws<PolisherPromptTooLongException>(() =>
            PolisherPromptBuilder.Build("重要な回答 2026.2", new string('根', 2000), 500));

        Assert.Contains("入力上限", error.Message);
    }

    [Fact]
    public void Validator_RequiresFocusedVersionAndCommandButNotUnrelatedSourceVersion()
    {
        var focus = new InquiryFocus { TargetVersions = ["2026.2"] };
        const string draft = "2026.2では qacli validate build を実行します。";
        const string context = draft + " 旧版は2025.1。";

        Assert.False(PolishedAnswerValidator.PreservesProtectedValues(context, draft,
            "qacli validate build を実行します。", focus));
        Assert.False(PolishedAnswerValidator.PreservesProtectedValues(context, draft,
            "2026.2で確認します。", focus));
        Assert.True(PolishedAnswerValidator.PreservesProtectedValues(context, draft,
            "2026.2では qacli validate build を実行してください。", focus));
    }

    [Fact]
    public void Validator_PreservesOriginalTechnicalHeaderName()
    {
        const string context = "お客様はＣＲＯＳヘッダについて質問しています。";
        const string draft = "ＣＲＯＳヘッダの動作を確認します。";

        Assert.Contains("ＣＲＯＳヘッダ", PolishedAnswerValidator.ExtractProtectedValues(context));
        Assert.True(PolishedAnswerValidator.PreservesProtectedValues(context, draft,
            "ＣＲＯＳヘッダの動作を確認します。", null));
        Assert.False(PolishedAnswerValidator.PreservesProtectedValues(context, draft,
            "CR-Oヘッダの動作を確認します。", null));
    }

    [Fact]
    public void PolisherPrompt_ForbidsTechnicalAdditions()
    {
        var prompt = PolisherPromptBuilder.Build("qacli validate build --qaf-project .");

        Assert.Contains("文章校正", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("新しい技術情報", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("qacli validate build --qaf-project .", prompt.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PolisherPrompt_UsesParserCompatibleJsonSchema()
    {
        var prompt = PolisherPromptBuilder.Build("回答案 2026.2");
        Assert.Contains("customerReplyDraft", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.NotNull(prompt.OutputSchema);
        var settings = new LlmProviderSettings
        {
            ChatModel = "qwen3:8b",
            StructuredOutputMode = StructuredOutputModes.Json,
        };
        var body = OllamaRequestBuilder.BuildChatRequestBody(settings,
            prompt.SystemPrompt, prompt.UserPrompt, true, prompt.OutputSchema);
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var format = request.RootElement.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.Equal("customerReplyDraft", format.GetProperty("required")[0].GetString());
        Assert.False(format.GetProperty("additionalProperties").GetBoolean());
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
