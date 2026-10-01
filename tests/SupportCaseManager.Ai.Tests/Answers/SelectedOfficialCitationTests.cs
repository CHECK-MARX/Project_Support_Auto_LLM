using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class SelectedOfficialCitationTests
{
    [Theory]
    [InlineData("Azure SQL Managed Instanceの可否を質問しています。", "Azure Managed Instance DBaaS is supported from Checkmarx SAST 9.2.")]
    [InlineData("チェッカー構成と既定enabledの変更を確認します。", "Disabled checkers If you chose to migrate your projects_root directory, verify that you have the same checker configuration as the previous release before your first integration build analysis .")]
    public void Prompt_UsesVerifiedFactSpanDespiteSurfaceDifferencesAndPreservesDecimalAndTiming(string inquiry, string span)
    {
        var source = new SearchSource
        {
            SourceId = "official:synthetic", SourceType = "OfficialDoc",
            Url = "https://example.test/release", Text = "Release notes 2026.2. " + span,
        };
        var resolution = new FactResolutionResult
        {
            AnswerReadiness = "NeedsManufacturerConfirmation",
            ResolvedFacts = SelectedOfficialFactProjector.Project(inquiry, [source]),
        };
        Assert.Single(resolution.ResolvedFacts);
        var prompt = GroundedAnswerPromptBuilder.Build(new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "Synthetic" }, InquiryText = inquiry,
            FactResolution = resolution, Sources = [source], Settings = new AiAssistantSettings { MaxPromptChars = 10000 },
        }, [source]);
        Assert.Contains("excerpt=「" + span + "」", prompt.UserPrompt);
        Assert.DoesNotContain("原文に一致する引用候補なし", prompt.UserPrompt);
        var alternatives = prompt.OutputSchema!.Value.GetProperty("properties")
            .GetProperty("evidence").GetProperty("items").GetProperty("oneOf");
        Assert.Contains(alternatives.EnumerateArray(), choice => choice.GetProperty("properties")
            .GetProperty("excerpt").GetProperty("const").GetString() == span);
        Assert.True(GroundedAnswerPromptBuilder.CitationMatchesSubject(inquiry, span, source, resolution));
        Assert.True(GroundedAnswerPromptBuilder.CitationMatchesSubject(span, span, source, resolution));
        Assert.False(GroundedAnswerPromptBuilder.CitationMatchesSubject("VPNライセンスサーバーの接続", span, source, resolution));
        Assert.False(GroundedAnswerPromptBuilder.CitationMatchesSubject(inquiry, "存在しない原文span", source, resolution));
    }

    [Fact]
    public void Prompt_DoesNotPromoteCurrentCaseOrTrustAFactWhoseSpanIsAbsent()
    {
        var source = new SearchSource { SourceId = "s1", SourceType = "OfficialDoc", Text = "無関係な資料であり、正確な支持spanはありません。" };
        var resolution = new FactResolutionResult
        {
            AnswerReadiness = "NeedsReview",
            ResolvedFacts = [new ResolvedFact
            {
                Key = "SelectedOfficialStatement", SourceType = "CurrentCase", Status = "Confirmed",
                EvidenceId = "s1", Value = "Azure Managed Instance DBaaS is supported from Checkmarx SAST 9.2.",
            }],
        };
        Assert.False(GroundedAnswerPromptBuilder.CitationMatchesSubject("Azure SQL Managed Instance", resolution.ResolvedFacts[0].Value, source, resolution));
        var official = resolution with { ResolvedFacts = [resolution.ResolvedFacts[0] with { SourceType = "OfficialDoc" }] };
        Assert.False(GroundedAnswerPromptBuilder.CitationMatchesSubject("Azure SQL Managed Instance", official.ResolvedFacts[0].Value, source, official));
    }
}
