using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Settings;

namespace SupportCaseManager.Ai.Tests.Settings;

public sealed class ProductKnowledgeSettingsSynchronizerTests
{
    [Fact]
    public void Synchronize_PreservesAiManualFoldersAndDocumentUrls()
    {
        var current = CreateSettings();
        var synchronizer = new ProductKnowledgeSettingsSynchronizer();

        var synchronized = synchronizer.Synchronize(
            current,
            [
                new SupportToolProductSettings
                {
                    ProductName = "HelixQAC",
                    BaseFolder = @"D:\Support\HelixQAC-New",
                    CloseFolder = @"D:\Support\HelixQAC-New\Closed",
                },
            ]);

        var product = Assert.Single(synchronized.Products);
        Assert.Equal(@"D:\Support\HelixQAC-New", product.BaseFolder);
        Assert.Equal(@"D:\Support\HelixQAC-New\Closed", product.CloseFolder);
        Assert.Contains(@"D:\Manuals\HelixQAC", product.ManualFolders);
        Assert.Contains("https://example.test/helixqac", product.DocumentUrls);
    }

    [Fact]
    public void Synchronize_AddsNewSupportToolProducts()
    {
        var current = CreateSettings();
        var synchronizer = new ProductKnowledgeSettingsSynchronizer();

        var synchronized = synchronizer.Synchronize(
            current,
            [
                new SupportToolProductSettings { ProductName = "HelixQAC", BaseFolder = @"D:\Helix", CloseFolder = @"D:\Helix\Closed" },
                new SupportToolProductSettings { ProductName = "Checkmarx", BaseFolder = @"D:\Checkmarx", CloseFolder = @"D:\Checkmarx\Closed" },
            ]);

        Assert.Equal(2, synchronized.Products.Count);
        Assert.Contains(synchronized.Products, product => product.ProductName == "Checkmarx");
        var newProduct = synchronized.Products.Single(product => product.ProductName == "Checkmarx");
        Assert.Empty(newProduct.ManualFolders);
        Assert.Empty(newProduct.DocumentUrls);
    }

    [Fact]
    public void Synchronize_DoesNotDeleteAiOnlyProducts()
    {
        var current = CreateSettings() with
        {
            Products =
            [
                CreateProduct("HelixQAC"),
                CreateProduct("AiOnly"),
            ],
        };
        var synchronizer = new ProductKnowledgeSettingsSynchronizer();

        var synchronized = synchronizer.Synchronize(
            current,
            [
                new SupportToolProductSettings { ProductName = "HelixQAC", BaseFolder = @"D:\Helix", CloseFolder = @"D:\Helix\Closed" },
            ]);

        Assert.Contains(synchronized.Products, product => product.ProductName == "AiOnly");
    }

    [Fact]
    public void Synchronize_MatchesProductNamesCaseInsensitively()
    {
        var current = CreateSettings();
        var synchronizer = new ProductKnowledgeSettingsSynchronizer();

        var synchronized = synchronizer.Synchronize(
            current,
            [
                new SupportToolProductSettings { ProductName = "helixqac", BaseFolder = @"D:\Helix", CloseFolder = @"D:\Helix\Closed" },
            ]);

        var product = Assert.Single(synchronized.Products);
        Assert.Equal("helixqac", product.ProductName);
        Assert.Contains(@"D:\Manuals\HelixQAC", product.ManualFolders);
    }

    [Fact]
    public void Synchronize_MigratesLegacyManualFolderWhenProductsAreEmpty()
    {
        var current = new AiAssistantSettings
        {
            DefaultProductName = "LegacyProduct",
            BaseFolder = @"D:\Legacy",
            CloseFolder = @"D:\Legacy\Closed",
            ManualFolder = @"D:\Manuals\Legacy",
        };
        var synchronizer = new ProductKnowledgeSettingsSynchronizer();

        var synchronized = synchronizer.Synchronize(current, []);

        var product = Assert.Single(synchronized.Products);
        Assert.Equal("LegacyProduct", product.ProductName);
        Assert.Equal(@"D:\Manuals\Legacy", Assert.Single(product.ManualFolders));
    }

    private static AiAssistantSettings CreateSettings()
    {
        return new AiAssistantSettings
        {
            SelectedProductName = "HelixQAC",
            Products = [CreateProduct("HelixQAC")],
        };
    }

    private static ProductKnowledgeSettings CreateProduct(string productName)
    {
        return new ProductKnowledgeSettings
        {
            ProductName = productName,
            BaseFolder = @"D:\Support\HelixQAC",
            CloseFolder = @"D:\Support\HelixQAC\Closed",
            ManualFolders = [@"D:\Manuals\HelixQAC"],
            DocumentUrls = ["https://example.test/helixqac"],
            IsEnabled = true,
        };
    }
}
