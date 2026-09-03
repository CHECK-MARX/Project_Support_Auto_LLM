using SupportCaseManager.Ai.Core.Artifacts;
using Xunit;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase65AcceptanceBindingTests
{
    [Fact]
    public void ManufacturerPromptContextCarriesBoundedCurrentCaseProvenance()
    {
        var context = new ArtifactPromptContext
        {
            CurrentCaseEvidenceReferences = "- EvidenceId: current:session:file:1\n  File: case.pdf\n  Locator: page:2\n  ContentHash: hash",
        };

        Assert.Contains("EvidenceId", context.CurrentCaseEvidenceReferences);
        Assert.Contains("Locator: page:2", context.CurrentCaseEvidenceReferences);
        Assert.DoesNotContain("C:\\", context.CurrentCaseEvidenceReferences);
    }
}
