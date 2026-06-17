using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Tests.Contracts;

public sealed class AiAssistantSettingsTests
{
    [Fact]
    public void DisableThinking_DefaultsToTrue()
    {
        var settings = new AiAssistantSettings();

        Assert.True(settings.DisableThinking);
        Assert.True(settings.SkipGenerationWhenNoEvidence);
        Assert.True(settings.EnableTopNFallback);
    }
}
