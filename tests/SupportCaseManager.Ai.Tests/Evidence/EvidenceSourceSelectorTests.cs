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
