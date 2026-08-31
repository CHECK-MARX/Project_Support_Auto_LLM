using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Tests.Ranking;

public sealed class Phase38QueryProfileTests
{
    public static TheoryData<string> AnalysisHowToQueries => new()
    {
        "QACで、プロジェクトを解析するための手順を教えてください。",
        "QACのプロジェクト解析をコマンドラインで実行する方法を教えてください。",
        "QACの解析CLIと主要なオプションを教えてください。",
        "QACでプロジェクト解析を自動化する場合のCLI手順を教えてください。",
        "QACのAnalysisをCLIで実行する方法を教えてください。",
    };

    [Theory]
    [MemberData(nameof(AnalysisHowToQueries))]
    public void AnalysisHowToQueryVariants_ProduceAtomicCoverageRequirements(string question)
    {
        var profile = TopicEntityAnalyzer.Extract(question, SupportTopicCatalog.Create("HelixQAC"));

        Assert.Contains("Analysis", profile.Operations);
        Assert.Contains("HowTo", profile.Intents);
        Assert.Contains(CoverageAnalyzer.AnalysisCommand,
            CoverageAnalyzer.RequiredForCoverageSelection(question, profile));
    }
}
