using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerOutputContractTests
{
    [Fact]
    public void ContractFollowsStyleInstructionsAndRequiresActualSubjectAndIdentity()
    {
        var context = new ManufacturerMailCaseContext
        {
            ProductName = "ExampleProduct", SupportId = "00012345",
            ProtectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet(
                "ExampleProduct", "00012345", "Questions.xlsx", ["Questions.xlsx"], "version 9.7.4.0"),
        };
        var prompt = ArtifactPromptComposer.AppendManufacturerOutputContract("STYLE: keep existing subject", context);
        Assert.StartsWith("STYLE: keep existing subject", prompt);
        Assert.Contains("既存の件名が入力にない場合", prompt);
        Assert.Contains("仮文・記入指示をメール本文に出力しない", prompt);
        Assert.Contains("丸括弧内", prompt);
        Assert.True(context.ProtectedValues.EvaluatePromptInjection(prompt).PromptParity);
    }

    [Fact]
    public void RepairIncludesBothPreviousDraftsAndExplicitValidationFailure()
    {
        var pair = new ManufacturerDraftPair { JapaneseDraft = "確認依頼", EnglishDraft = "Review request" };
        var prompt = ArtifactPromptComposer.ComposeManufacturerRepairPrompt("APPROVED CONTEXT", pair, ["Missing support ID"]);
        Assert.StartsWith("APPROVED CONTEXT", prompt);
        Assert.Contains("Review request", prompt);
        Assert.Contains("Missing support ID", prompt);
        Assert.Contains("新しい事実・質問・終了依頼は追加しない", prompt);
        Assert.Contains("両方をJSON", prompt);
    }
}
