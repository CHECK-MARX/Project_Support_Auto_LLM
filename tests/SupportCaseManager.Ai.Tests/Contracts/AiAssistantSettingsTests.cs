using SupportCaseManager.Ai.Contracts;
using System.Text.Json;

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
        Assert.False(settings.UseQuestionAwareEvidenceSelection);
        Assert.Equal(EvidenceRankingModes.Phase15, settings.EvidenceRankingMode);
    }

    [Fact]
    public void EvidenceRankingMode_OldJsonDefaultsToPhase15AndPhase16RoundTrips()
    {
        var oldSettings = JsonSerializer.Deserialize<AiAssistantSettings>(
            """{"useQuestionAwareEvidenceSelection":true}""");
        var phase16 = new AiAssistantSettings
        {
            UseQuestionAwareEvidenceSelection = true,
            EvidenceRankingMode = EvidenceRankingModes.Phase16,
        };
        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(
            JsonSerializer.Serialize(phase16));

        Assert.NotNull(oldSettings);
        Assert.Equal(EvidenceRankingModes.Phase15, oldSettings.EvidenceRankingMode);
        Assert.NotNull(restored);
        Assert.Equal(EvidenceRankingModes.Phase16, restored.EvidenceRankingMode);
    }
}
