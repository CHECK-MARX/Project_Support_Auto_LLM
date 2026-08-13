using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Settings;

public class AiSettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefaultSettingsWhenFileDoesNotExist()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiSettingsStore();

        var settings = await store.LoadAsync(aiDataFolder);

        Assert.Equal(aiDataFolder, settings.AiDataFolder);
        Assert.Equal(2, settings.MaxEvidenceItems);
        Assert.Equal(0.30, settings.AutoSelectMinimumScore);
        Assert.Equal(0, settings.MinimumDisplayScore);
        Assert.Equal(6000, settings.MaxPromptChars);
        Assert.False(settings.EnableCloudLlm);
        Assert.True(settings.MaskSensitiveDataForCloud);
        Assert.True(settings.DisableThinking);
        Assert.True(settings.SkipGenerationWhenNoEvidence);
        Assert.True(settings.EnableTopNFallback);
        Assert.False(settings.UseRagLabEvidence);
        Assert.False(settings.UseAnswerQualityGate);
        Assert.Equal(3, settings.RagLabEvidenceMaxItems);
        Assert.Empty(settings.RagLabEvidenceFilePath);
        Assert.Empty(settings.RagLabBaselineReadinessFilePath);
        Assert.Equal("Ollama", settings.LlmProvider.Provider);
        Assert.False(File.Exists(System.IO.Path.Combine(aiDataFolder, "settings.json")));
    }

    [Fact]
    public async Task SaveAsync_CanBeLoadedBack()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiSettingsStore();
        var settings = new AiAssistantSettings
        {
            AiDataFolder = aiDataFolder,
            AiIndexFolder = System.IO.Path.Combine(temp.Path, "ai-index"),
            BaseFolder = @"D:\Support\Cases",
            CloseFolder = @"D:\Support\Closed",
            DefaultProductName = "製品A",
            MaxEvidenceItems = 5,
            AutoSelectMinimumScore = 0.7,
            MinimumDisplayScore = 0.25,
            MaxPromptChars = 12000,
            SkipGenerationWhenNoEvidence = false,
            EnableTopNFallback = false,
            UseRagLabEvidence = true,
            UseAnswerQualityGate = true,
            RagLabEvidenceFilePath = Path.Combine(temp.Path, "evidence.json"),
            RagLabBaselineReadinessFilePath = Path.Combine(temp.Path, "readiness.json"),
            RagLabEvidenceMaxItems = 5,
            AnswerQualityMode = AnswerQualityModes.Quality,
            ModelCapabilityProfiles =
            [
                new ModelCapabilityProfile
                {
                    ModelName = "gemma4:31b",
                    ThinkingParameterType = ThinkingParameterTypes.None,
                    StructuredOutputMode = StructuredOutputModes.PlainText,
                },
            ],
            LlmProvider = new LlmProviderSettings
            {
                Provider = "Ollama",
                Endpoint = "http://localhost:11434",
                ChatModel = "llama3.2",
                EmbeddingModel = "nomic-embed-text",
                ContextWindowTokens = 16384,
                ApiKeyEnvironmentVariable = "SUPPORT_AI_API_KEY",
            },
        };

        await store.SaveAsync(settings);
        var restored = await store.LoadAsync(aiDataFolder);

        Assert.Equal(settings.AiDataFolder, restored.AiDataFolder);
        Assert.Equal(settings.AiIndexFolder, restored.AiIndexFolder);
        Assert.Equal(settings.BaseFolder, restored.BaseFolder);
        Assert.Equal(settings.CloseFolder, restored.CloseFolder);
        Assert.Equal(settings.DefaultProductName, restored.DefaultProductName);
        Assert.Equal(settings.MaxEvidenceItems, restored.MaxEvidenceItems);
        Assert.Equal(settings.AutoSelectMinimumScore, restored.AutoSelectMinimumScore);
        Assert.Equal(settings.MinimumDisplayScore, restored.MinimumDisplayScore);
        Assert.Equal(settings.MaxPromptChars, restored.MaxPromptChars);
        Assert.Equal(settings.SkipGenerationWhenNoEvidence, restored.SkipGenerationWhenNoEvidence);
        Assert.Equal(settings.EnableTopNFallback, restored.EnableTopNFallback);
        Assert.True(restored.UseRagLabEvidence);
        Assert.True(restored.UseAnswerQualityGate);
        Assert.Equal(settings.RagLabEvidenceFilePath, restored.RagLabEvidenceFilePath);
        Assert.Equal(settings.RagLabBaselineReadinessFilePath, restored.RagLabBaselineReadinessFilePath);
        Assert.Equal(5, restored.RagLabEvidenceMaxItems);
        Assert.Equal(AnswerQualityModes.Quality, restored.AnswerQualityMode);
        var profile = Assert.Single(restored.ModelCapabilityProfiles);
        Assert.Equal("gemma4:31b", profile.ModelName);
        Assert.Equal(ThinkingParameterTypes.None, profile.ThinkingParameterType);
        Assert.Equal("llama3.2", restored.LlmProvider.ChatModel);
        Assert.Equal("nomic-embed-text", restored.LlmProvider.EmbeddingModel);
        Assert.Equal(16384, restored.LlmProvider.ContextWindowTokens);
        Assert.Equal("SUPPORT_AI_API_KEY", restored.LlmProvider.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public async Task LoadAsync_LegacySettingsWithoutRagLabProperties_DefaultsEvidenceIntegrationOff()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = Path.Combine(temp.Path, "ai-data");
        Directory.CreateDirectory(aiDataFolder);
        await File.WriteAllTextAsync(
            Path.Combine(aiDataFolder, "settings.json"),
            $$"""
            {
              "aiDataFolder": {{JsonSerializer.Serialize(aiDataFolder)}},
              "maxEvidenceItems": 2
            }
            """);

        var restored = await new AiSettingsStore().LoadAsync(aiDataFolder);

        Assert.False(restored.UseRagLabEvidence);
        Assert.Equal(3, restored.RagLabEvidenceMaxItems);
        Assert.Empty(restored.RagLabEvidenceFilePath);
        Assert.Empty(restored.RagLabBaselineReadinessFilePath);
    }

    [Fact]
    public async Task SaveAsync_PreservesProductManualFoldersAndDocumentUrls()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiSettingsStore();
        var settings = new AiAssistantSettings
        {
            AiDataFolder = aiDataFolder,
            SelectedProductName = "HelixQAC",
            Products =
            [
                new ProductKnowledgeSettings
                {
                    ProductName = "HelixQAC",
                    BaseFolder = @"D:\Support\HelixQAC",
                    CloseFolder = @"D:\Support\HelixQAC\Closed",
                    ManualFolders = [@"D:\Manuals\HelixQAC", @"D:\Manuals\Common"],
                    DocumentUrls = ["https://example.test/helixqac", "https://example.test/common"],
                    IsEnabled = true,
                    CrawlMaxDepth = 3,
                    CrawlMaxPages = 250,
                },
            ],
        };

        await store.SaveAsync(settings);
        var restored = await store.LoadAsync(aiDataFolder);

        var product = Assert.Single(restored.Products);
        Assert.Equal("HelixQAC", product.ProductName);
        Assert.Equal(2, product.ManualFolders.Count);
        Assert.Contains(@"D:\Manuals\HelixQAC", product.ManualFolders);
        Assert.Contains(@"D:\Manuals\Common", product.ManualFolders);
        Assert.Equal(2, product.DocumentUrls.Count);
        Assert.Contains("https://example.test/helixqac", product.DocumentUrls);
        Assert.Contains("https://example.test/common", product.DocumentUrls);
        Assert.Equal(3, product.CrawlMaxDepth);
        Assert.Equal(250, product.CrawlMaxPages);
    }

    [Fact]
    public async Task SaveAsync_PreservesDisableThinking()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = Path.Combine(temp.Path, "ai-data");
        var store = new AiSettingsStore();
        var settings = new AiAssistantSettings
        {
            AiDataFolder = aiDataFolder,
            DisableThinking = false,
        };

        await store.SaveAsync(settings);
        var restored = await store.LoadAsync(aiDataFolder);

        Assert.False(restored.DisableThinking);
    }

    [Fact]
    public async Task SaveAsync_WritesSettingsJsonUnderAiDataFolder()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiSettingsStore();

        await store.SaveAsync(new AiAssistantSettings { AiDataFolder = aiDataFolder });

        Assert.True(File.Exists(System.IO.Path.Combine(aiDataFolder, "settings.json")));
        Assert.Empty(Directory.EnumerateFiles(aiDataFolder, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_DoesNotCreateExistingAppSettingsFiles()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiSettingsStore();

        await store.SaveAsync(new AiAssistantSettings { AiDataFolder = aiDataFolder });

        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "user-settings.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "cases-index.json")));
    }

    [Fact]
    public async Task SaveAsync_StoresApiKeyEnvironmentVariableNameOnly()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiSettingsStore();
        var settings = new AiAssistantSettings
        {
            AiDataFolder = aiDataFolder,
            LlmProvider = new LlmProviderSettings
            {
                ApiKeyEnvironmentVariable = "SUPPORT_AI_API_KEY",
            },
        };

        await store.SaveAsync(settings);

        var json = await File.ReadAllTextAsync(System.IO.Path.Combine(aiDataFolder, "settings.json"));
        using var document = JsonDocument.Parse(json);
        var provider = document.RootElement.GetProperty("llmProvider");
        Assert.Equal("SUPPORT_AI_API_KEY", provider.GetProperty("apiKeyEnvironmentVariable").GetString());
        Assert.DoesNotContain("sk-", json, StringComparison.OrdinalIgnoreCase);
    }
}
