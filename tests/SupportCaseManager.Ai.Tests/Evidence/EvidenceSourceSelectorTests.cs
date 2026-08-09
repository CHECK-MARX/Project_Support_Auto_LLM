using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class EvidenceSourceSelectorTests
{
    [Fact]
    public void Select_KeepsDistinctProcedureChunksFromSamePdfTitle()
    {
        var sources = new[]
        {
            CreateSource("gui", "Perforce_QAC_Manual", "［ポータル］>［Validate］>［解析結果をアップロード］を選択します。", 0.91),
            CreateSource("cli", "Perforce_QAC_Manual", "qacli validate build --qaf-project . を実行します。", 0.90),
        };
        var facts = new FactResolutionResult
        {
            Classification = new QuestionClassificationResult
            {
                QuestionTypes = [QuestionTypes.HowToQuestion],
            },
        };

        var selected = EvidenceSourceSelector.Select(
            sources,
            new CaseContext { ProductName = "HelixQAC" },
            facts,
            maxItems: 3,
            maxPromptChars: 6000);

        Assert.Equal(["gui", "cli"], selected.Select(static source => source.SourceId));
    }

    [Fact]
    public void Select_KeepsThreeLongDistinctSectionsFromSameUrl()
    {
        var sources = new[]
        {
            CreateSource("overview", "Perforce_QAC_Manual", $"Validate Stream overview {new string('A', 1100)}", 0.92) with { SectionTitle = "Stream overview", Url = "file:///manual.pdf" },
            CreateSource("setup", "Perforce_QAC_Manual", $"Validate Stream setup {new string('B', 1100)}", 0.91) with { SectionTitle = "Stream setup", Url = "file:///manual.pdf" },
            CreateSource("verify", "Perforce_QAC_Manual", $"Validate Stream verification {new string('C', 1100)}", 0.90) with { SectionTitle = "Stream verification", Url = "file:///manual.pdf" },
        };
        var facts = new FactResolutionResult
        {
            Classification = new QuestionClassificationResult
            {
                QuestionTypes = [QuestionTypes.HowToQuestion, QuestionTypes.ConfigurationQuestion],
            },
        };

        var selected = EvidenceSourceSelector.Select(
            sources,
            new CaseContext { ProductName = "HelixQAC" },
            facts,
            maxItems: 3,
            maxPromptChars: 6000);

        Assert.Equal(["overview", "setup", "verify"], selected.Select(static source => source.SourceId));
    }

    private static SearchSource CreateSource(string id, string title, string text, double score)
    {
        return new SearchSource
        {
            SourceId = id,
            SourceType = "Manual",
            ProductName = "HelixQAC",
            Title = title,
            Text = text,
            Score = score,
        };
    }
}
