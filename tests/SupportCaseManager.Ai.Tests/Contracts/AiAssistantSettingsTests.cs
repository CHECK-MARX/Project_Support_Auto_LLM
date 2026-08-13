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
        Assert.False(settings.UseAnswerQualityGate);
        Assert.False(settings.UsePhase175QualityControls);
        Assert.False(settings.UseCoverageAwareEvidenceSelection);
        Assert.Equal(5, settings.CoverageAwareMaxEvidenceItems);
        Assert.True(settings.UseRustEvidenceSelector);
        Assert.False(settings.UsePersistentRustEvidenceSelector);
        Assert.Equal(3, settings.MaxWorkerRestartsPerMinute);
        Assert.False(settings.EnableRustSelectorShadowMode);
        Assert.Equal(2000, settings.RustEvidenceSelectorTimeoutMs);
        Assert.Empty(settings.RustEvidenceSelectorExecutablePath);
        Assert.Equal(50, settings.ShadowMinimumRunsForReadiness);
        Assert.Equal(500, settings.ShadowMaxStoredRecords);
        Assert.Equal(EvidenceRankingModes.Phase15, settings.EvidenceRankingMode);
    }

    [Fact]
    public void RustSelectorSettings_OldJsonDefaultsToProductionPreferredAndRoundTrips()
    {
        var oldSettings = JsonSerializer.Deserialize<AiAssistantSettings>("{}");
        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(JsonSerializer.Serialize(new AiAssistantSettings
        {
            UseRustEvidenceSelector = true,
            UsePersistentRustEvidenceSelector = true,
            MaxWorkerRestartsPerMinute = 5,
            EnableRustSelectorShadowMode = true,
            RustEvidenceSelectorTimeoutMs = 1200,
            RustEvidenceSelectorExecutablePath = @"C:\tools\rag-selector-rs.exe",
            ShadowMinimumRunsForReadiness = 75,
            ShadowMaxStoredRecords = 700,
        }));

        Assert.NotNull(oldSettings);
        Assert.True(oldSettings.UseRustEvidenceSelector);
        Assert.False(oldSettings.UsePersistentRustEvidenceSelector);
        Assert.Equal(3, oldSettings.MaxWorkerRestartsPerMinute);
        Assert.False(oldSettings.EnableRustSelectorShadowMode);
        Assert.Equal(2000, oldSettings.RustEvidenceSelectorTimeoutMs);
        Assert.Equal(50, oldSettings.ShadowMinimumRunsForReadiness);
        Assert.Equal(500, oldSettings.ShadowMaxStoredRecords);
        Assert.NotNull(restored);
        Assert.True(restored.UseRustEvidenceSelector);
        Assert.True(restored.UsePersistentRustEvidenceSelector);
        Assert.Equal(5, restored.MaxWorkerRestartsPerMinute);
        Assert.True(restored.EnableRustSelectorShadowMode);
        Assert.Equal(1200, restored.RustEvidenceSelectorTimeoutMs);
        Assert.Equal(@"C:\tools\rag-selector-rs.exe", restored.RustEvidenceSelectorExecutablePath);
        Assert.Equal(75, restored.ShadowMinimumRunsForReadiness);
        Assert.Equal(700, restored.ShadowMaxStoredRecords);
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
        Assert.False(restored.UseAnswerQualityGate);
    }

    [Fact]
    public void UseAnswerQualityGate_RoundTrips()
    {
        var settings = new AiAssistantSettings { UseAnswerQualityGate = true };

        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(
            JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.True(restored.UseAnswerQualityGate);
    }

    [Fact]
    public void UsePhase175QualityControls_OldJsonDefaultsOffAndRoundTrips()
    {
        var oldSettings = JsonSerializer.Deserialize<AiAssistantSettings>("{}");
        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(
            JsonSerializer.Serialize(new AiAssistantSettings { UsePhase175QualityControls = true }));

        Assert.NotNull(oldSettings);
        Assert.False(oldSettings.UsePhase175QualityControls);
        Assert.NotNull(restored);
        Assert.True(restored.UsePhase175QualityControls);
    }

    [Fact]
    public void CoverageAwareSelection_OldJsonDefaultsOffAndRoundTrips()
    {
        var oldSettings = JsonSerializer.Deserialize<AiAssistantSettings>("{}");
        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(
            JsonSerializer.Serialize(new AiAssistantSettings
            {
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 4,
            }));

        Assert.NotNull(oldSettings);
        Assert.False(oldSettings.UseCoverageAwareEvidenceSelection);
        Assert.Equal(5, oldSettings.CoverageAwareMaxEvidenceItems);
        Assert.NotNull(restored);
        Assert.True(restored.UseCoverageAwareEvidenceSelection);
        Assert.Equal(4, restored.CoverageAwareMaxEvidenceItems);
    }
}
