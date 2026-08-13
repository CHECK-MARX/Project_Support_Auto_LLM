using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexDraftTextApplicatorTests
{
    [Fact]
    public void Apply_OnlyReturnsMemoryTextAndDoesNotModifyCaseFile()
    {
        using var temp = new TempDirectory();
        var caseFile = Path.Combine(temp.Path, "reply.txt");
        File.WriteAllText(caseFile, "original");

        var result = CodexDraftTextApplicator.Apply("existing", "Codex answer", CodexDraftApplyMode.Append);

        Assert.Equal($"existing{Environment.NewLine}{Environment.NewLine}Codex answer", result);
        Assert.Equal("original", File.ReadAllText(caseFile));
    }
}
