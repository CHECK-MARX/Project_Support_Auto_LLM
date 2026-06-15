using System.Text.Json;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Tests.Contracts;

public sealed class AiAssistantSettingsAppearanceTests
{
    [Fact]
    public void AppearanceDefaults_AreJapaneseLightMode()
    {
        var settings = new AiAssistantSettings();

        Assert.Equal("ja-JP", settings.UiLanguage);
        Assert.False(settings.UseDarkMode);
    }

    [Fact]
    public void AppearanceSettings_RoundTripThroughJson()
    {
        var settings = new AiAssistantSettings
        {
            AiDataFolder = @"D:\Support\ai-data",
            UiLanguage = "en-US",
            UseDarkMode = true,
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(json);

        Assert.Contains("\"uiLanguage\"", json);
        Assert.Contains("\"useDarkMode\"", json);
        Assert.NotNull(restored);
        Assert.Equal("en-US", restored.UiLanguage);
        Assert.True(restored.UseDarkMode);
    }
}
