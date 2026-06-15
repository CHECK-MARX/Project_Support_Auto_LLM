using System.Text.Json;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Tests.Contracts;

public sealed class ProductKnowledgeSettingsTests
{
    [Fact]
    public void AiAssistantSettings_RoundTripsProductKnowledgeSettings()
    {
        var settings = new AiAssistantSettings
        {
            AiDataFolder = @"D:\Support\ai-data",
            AiIndexFolder = @"D:\Support\ai-index",
            SupportToolSettingsFilePath = @"D:\Support\config\user-settings.json",
            SelectedProductName = "HelixQAC",
            Products =
            [
                new ProductKnowledgeSettings
                {
                    ProductName = "HelixQAC",
                    BaseFolder = @"D:\Support\HelixQAC",
                    CloseFolder = @"D:\Support\HelixQAC\Closed",
                    ManualFolders =
                    [
                        @"D:\Manuals\HelixQAC",
                        @"D:\Manuals\Common",
                    ],
                    DocumentUrls =
                    [
                        "https://example.test/helixqac/manual",
                        "https://example.test/helixqac/faq",
                    ],
                    IsEnabled = true,
                },
            ],
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(@"D:\Support\config\user-settings.json", restored.SupportToolSettingsFilePath);
        Assert.Equal("HelixQAC", restored.SelectedProductName);
        var product = Assert.Single(restored.Products);
        Assert.Equal("HelixQAC", product.ProductName);
        Assert.Equal(2, product.ManualFolders.Count);
        Assert.Contains(@"D:\Manuals\Common", product.ManualFolders);
        Assert.Equal(2, product.DocumentUrls.Count);
        Assert.Contains("https://example.test/helixqac/faq", product.DocumentUrls);
        Assert.True(product.IsEnabled);
    }

    [Fact]
    public void ProductKnowledgeSettings_DefaultValuesAreSafe()
    {
        var settings = new ProductKnowledgeSettings();

        Assert.Equal(string.Empty, settings.ProductName);
        Assert.Equal(string.Empty, settings.BaseFolder);
        Assert.Equal(string.Empty, settings.CloseFolder);
        Assert.Empty(settings.ManualFolders);
        Assert.Empty(settings.DocumentUrls);
        Assert.True(settings.IsEnabled);
    }
}
