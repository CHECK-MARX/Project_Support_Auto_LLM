using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Settings;

public sealed class ProductDefinitionSharingTests
{
    [Fact]
    public async Task Reader_ReadsNewProductDefinitionFieldsAndSorts()
    {
        using var temp = new TempDirectory();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            $$"""
              {
                "Products": [
                  {
                    "Id": "{{secondId}}",
                    "DisplayName": "Second",
                    "Aliases": ["S2"],
                    "BaseFolder": "D:\\Second",
                    "ClosedFolder": "D:\\Second\\Closed",
                    "ProductPromptFilePath": "prompts/products/second.txt",
                    "IsEnabled": false,
                    "SortOrder": 20
                  },
                  {
                    "Id": "{{firstId}}",
                    "DisplayName": "First",
                    "BaseFolder": "D:\\First",
                    "ClosedFolder": "D:\\First\\Closed",
                    "SortOrder": 10
                  }
                ]
              }
              """,
            Encoding.UTF8);

        var products = await new SupportToolSettingsReader().ReadProductsAsync(settingsPath);

        Assert.Equal(["First", "Second"], products.Select(product => product.ProductName));
        var second = products[1];
        Assert.Equal(secondId, second.Id);
        Assert.Equal(["S2"], second.Aliases);
        Assert.Equal("prompts/products/second.txt", second.ProductPromptFilePath);
        Assert.False(second.IsEnabled);
        Assert.Equal(20, second.SortOrder);
    }

    [Fact]
    public async Task Reader_ReadsShiftJisLegacySettings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        const string json = """
            {
              "Products": [
                {
                  "Name": "日本語製品",
                  "BasePath": "D:\\製品",
                  "ClosedPath": "D:\\製品\\クローズ"
                }
              ]
            }
            """;
        await File.WriteAllBytesAsync(settingsPath, Encoding.GetEncoding(932).GetBytes(json));

        var products = await new SupportToolSettingsReader().ReadProductsAsync(settingsPath);

        Assert.Equal("日本語製品", Assert.Single(products).ProductName);
    }

    [Fact]
    public void Synchronizer_MatchesByIdAcrossRenameAndPreservesAiKnowledge()
    {
        var id = Guid.NewGuid();
        var current = new AiAssistantSettings
        {
            SelectedProductName = "OldName",
            DefaultProductName = "OldName",
            Products =
            [
                new ProductKnowledgeSettings
                {
                    ProductId = id,
                    ProductName = "OldName",
                    ManualFolders = [@"D:\Manuals\OldName"],
                    DocumentUrls = ["https://example.test/product"],
                    IsEnabled = true,
                },
            ],
        };
        var shared = new SupportToolProductSettings
        {
            Id = id,
            ProductName = "NewName",
            Aliases = ["OldName", "NN"],
            BaseFolder = @"D:\Support\NewName",
            CloseFolder = @"D:\Support\NewName\Closed",
            ProductPromptFilePath = "prompts/products/new-name.txt",
            IsEnabled = true,
            SortOrder = 7,
        };

        var synchronized = new ProductKnowledgeSettingsSynchronizer().Synchronize(current, [shared]);

        var product = Assert.Single(synchronized.Products);
        Assert.Equal(id, product.ProductId);
        Assert.Equal("NewName", product.ProductName);
        Assert.Contains(@"D:\Manuals\OldName", product.ManualFolders);
        Assert.Contains("https://example.test/product", product.DocumentUrls);
        Assert.Equal("prompts/products/new-name.txt", product.ProductPromptFilePath);
        Assert.Equal(7, product.SortOrder);
        Assert.Equal("NewName", synchronized.SelectedProductName);
        Assert.Equal("NewName", synchronized.DefaultProductName);
    }

    [Fact]
    public void Synchronizer_KeepsDisabledDefinitionButSelectsAnEnabledProduct()
    {
        var disabledId = Guid.NewGuid();
        var enabledId = Guid.NewGuid();
        var current = new AiAssistantSettings
        {
            SelectedProductName = "Disabled",
            DefaultProductName = "Disabled",
            Products =
            [
                new ProductKnowledgeSettings { ProductId = disabledId, ProductName = "Disabled", IsEnabled = true },
                new ProductKnowledgeSettings { ProductId = enabledId, ProductName = "Enabled", IsEnabled = true },
            ],
        };
        var shared = new SupportToolProductSettings[]
        {
            new() { Id = disabledId, ProductName = "Disabled", IsEnabled = false, SortOrder = 0 },
            new() { Id = enabledId, ProductName = "Enabled", IsEnabled = true, SortOrder = 1 },
        };

        var synchronized = new ProductKnowledgeSettingsSynchronizer().Synchronize(current, shared);

        Assert.False(synchronized.Products.Single(product => product.ProductId == disabledId).IsEnabled);
        Assert.Equal("Enabled", synchronized.SelectedProductName);
        Assert.Equal("Enabled", synchronized.DefaultProductName);
    }
}
