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
    public void Prompt_BoundsOutputQuotationsAndDeduplicatesOverlappingSourceSpans()
    {
        const string span = "Azure Managed Instance DBaaS is supported from Checkmarx SAST 9.2.";
        const string inquiry = "Azure SQL Managed Instanceの可否を質問しています。";
        var sources = new[]
        {
            new SearchSource { SourceId = "official:one", SourceType = "OfficialDoc", Url = "https://example.test/one", Text = span },
            new SearchSource { SourceId = "official:overlap", SourceType = "OfficialDoc", Url = "https://example.test/overlap", Text = span },
        };
        var prompt = GroundedAnswerPromptBuilder.Build(new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "Synthetic" }, InquiryText = inquiry,
            Sources = sources, Settings = new AiAssistantSettings { MaxPromptChars = 10000 },
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts = SelectedOfficialFactProjector.Project(inquiry, sources),
            },
        }, sources);
        var evidence = prompt.OutputSchema!.Value.GetProperty("properties").GetProperty("evidence");
        Assert.Equal(1, evidence.GetProperty("maxItems").GetInt32());
        Assert.True(evidence.GetProperty("uniqueItems").GetBoolean());
        var candidate = Assert.Single(evidence.GetProperty("items").GetProperty("oneOf").EnumerateArray());
        var properties = candidate.GetProperty("properties");
        Assert.Equal(sources[0].SourceId, properties.GetProperty("sourceId").GetProperty("const").GetString());
        Assert.Equal(span, properties.GetProperty("excerpt").GetProperty("const").GetString());
        Assert.All(sources, source => Assert.Equal(span, source.Text));
    }

    [Fact]
    public void Prompt_DoesNotOfferUnrelatedReleaseSectionsJustBecauseTheyShareVersion()
    {
        const string factSpan = "Disabled checkers If you chose to migrate your projects_root directory, verify that you have the same checker configuration as the previous release before your first integration build analysis .";
        const string otherSpan = "Licensing changes 2024 licenses are not compatible with Klocwork 2026.2 .";
        const string inquiry = "Klocwork 2025.2から2026.2へ移行し、既定enabledとチェッカー構成の変更を確認します。";
        var source = new SearchSource
        {
            SourceId = "official:release", SourceType = "OfficialDoc", Url = "https://example.test/release",
            Text = "Klocwork 2026.2 Release notes. " + factSpan + "\n" + otherSpan,
        };
        var prompt = GroundedAnswerPromptBuilder.Build(new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "Klocwork" }, InquiryText = inquiry,
            Sources = [source], Settings = new AiAssistantSettings { MaxPromptChars = 10000 },
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = SelectedOfficialFactProjector.Project(inquiry, [source]),
            },
        }, [source]);
        var evidence = prompt.OutputSchema!.Value.GetProperty("properties").GetProperty("evidence");
        var candidate = Assert.Single(evidence.GetProperty("items").GetProperty("oneOf").EnumerateArray());
        Assert.Equal(factSpan, candidate.GetProperty("properties").GetProperty("excerpt").GetProperty("const").GetString());
        Assert.Equal(1, evidence.GetProperty("maxItems").GetInt32());
        Assert.Contains(otherSpan, source.Text);
        Assert.Contains(source.Text, prompt.UserPrompt); // full frozen evidence remains unchanged
        Assert.DoesNotContain("excerpt=「" + otherSpan, prompt.UserPrompt);
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
