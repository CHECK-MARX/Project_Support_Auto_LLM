using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class CodexPromptPresetTests
{
    [Fact]
    public void Defaults_SeparateManufacturerQuestionAndReplyPresets()
    {
        var ask = Assert.Single(
            CodexPromptPreset.Defaults,
            static item => item.Name == "メーカーへ確認・追加質問する");
        var reply = Assert.Single(
            CodexPromptPreset.Defaults,
            static item => item.Name == "メーカー回答へ返信する（御礼・受領）");
        Assert.Contains("意味的に同一の英語", ask.Prompt, StringComparison.Ordinal);
        Assert.Contains("新しい技術質問", reply.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeLegacyPrompt_MapsSavedJapaneseAndEnglishPrompts()
    {
        Assert.Equal(
            CodexPromptPreset.ManufacturerConfirmationPrompt,
            CodexPromptPreset.NormalizeLegacyPrompt("メーカー向け日本語確認案を作成"));
        Assert.Equal(
            CodexPromptPreset.ManufacturerConfirmationPrompt,
            CodexPromptPreset.NormalizeLegacyPrompt("メーカーへ技術確認するための英語メール案を、事象、環境、確認済み事項、質問に分けて作成してください。"));
    }
}
