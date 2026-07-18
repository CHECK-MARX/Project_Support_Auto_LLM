using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Tests.Llm;

public sealed class ModelCapabilityProfileTests
{
    [Theory]
    [InlineData(AnswerQualityModes.Fast, "qwen3:8b")]
    [InlineData(AnswerQualityModes.Standard, "gemma4:26b")]
    [InlineData(AnswerQualityModes.Quality, "gemma4:31b")]
    public void ModelForQualityMode_ReturnsConfiguredModel(string qualityMode, string expectedModel)
    {
        Assert.Equal(expectedModel, ModelCapabilityProfiles.ModelForQualityMode(qualityMode));
    }

    [Theory]
    [InlineData("gemma4:26b")]
    [InlineData("gemma4:31b")]
    [InlineData("gpt-oss:120b-cloud")]
    public void Resolve_NonQwenProfileDoesNotSendThinkingOrJsonParameters(string modelName)
    {
        var profile = ModelCapabilityProfiles.Resolve(modelName);

        Assert.Equal(ThinkingParameterTypes.None, profile.ThinkingParameterType);
        Assert.Equal(StructuredOutputModes.PlainText, profile.StructuredOutputMode);
    }

    [Fact]
    public void Resolve_UsesCustomOverrideBeforeDefaults()
    {
        var profile = ModelCapabilityProfiles.Resolve(
            "qwen3:8b",
            [
                new ModelCapabilityProfile
                {
                    ModelName = "qwen3:8b",
                    MaxOutputTokens = 321,
                    TimeoutSeconds = 45,
                },
            ]);

        Assert.Equal(321, profile.MaxOutputTokens);
        Assert.Equal(45, profile.TimeoutSeconds);
    }
}
