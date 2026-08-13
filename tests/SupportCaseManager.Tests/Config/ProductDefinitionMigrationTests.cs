using System.Text;
using System.Text.Json;
using SupportCaseManager.Core.Config;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Config;

public sealed class ProductDefinitionMigrationTests
{
    [Fact]
    public void Load_MigratesLegacyThreeProductsAndCreatesBackup()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        File.WriteAllText(settingsPath, CreateLegacyJson(), Encoding.UTF8);

        var settings = new ConfigStore(temp.Path).Load();

        Assert.Equal(3, settings.Products.Count);
        Assert.Equal(ProductDefinitionDefaults.HelixQacId, settings.Products[0].Id);
        Assert.Equal(ProductDefinitionDefaults.CheckmarxId, settings.Products[1].Id);
        Assert.Equal(ProductDefinitionDefaults.KlocworkId, settings.Products[2].Id);
        Assert.Equal("prompts/products/qac.txt", settings.Products[0].ProductPromptFilePath);
        Assert.Equal("prompts/products/checkmarx.txt", settings.Products[1].ProductPromptFilePath);
        Assert.Equal("prompts/products/klocwork.txt", settings.Products[2].ProductPromptFilePath);
        Assert.Equal([0, 1, 2], settings.Products.Select(product => product.SortOrder));
        Assert.Equal(ProductDefinitionDefaults.HelixQacId, settings.ActiveProductId);
        Assert.True(File.Exists(settingsPath + ".pre-product-migration.bak"));

        using var migratedDocument = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
        Assert.True(migratedDocument.RootElement.GetProperty("Products")[0].TryGetProperty("DisplayName", out _));
        Assert.True(migratedDocument.RootElement.GetProperty("Products")[0].TryGetProperty("Id", out _));
    }

    [Fact]
    public void Load_MigratesShiftJisLegacySettings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        var json = CreateLegacyJson().Replace("D:\\\\Support", "D:\\\\株式会社サポート", StringComparison.Ordinal);
        File.WriteAllBytes(settingsPath, Encoding.GetEncoding(932).GetBytes(json));

        var settings = new ConfigStore(temp.Path).Load();

        Assert.Equal(3, settings.Products.Count);
        Assert.Contains("株式会社", settings.Products[0].BaseFolder, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAndLoad_AddsProductAndPreservesIdAfterRename()
    {
        using var temp = new TempDirectory();
        var store = new ConfigStore(temp.Path);
        var id = Guid.NewGuid();
        var settings = new UserSettings
        {
            Products =
            [
                new ProductProfile
                {
                    Id = id,
                    DisplayName = "NewProduct",
                    Aliases = ["NP"],
                    BaseFolder = @"D:\Support\NewProduct",
                    ClosedFolder = @"D:\Support\NewProduct\Closed",
                    ProductPromptFilePath = @"prompts\products\new-product.txt",
                    IsEnabled = true,
                    SortOrder = 4,
                },
            ],
            ActiveProduct = "NewProduct",
            ActiveProductId = id,
        };
        store.Save(settings);

        var renamed = store.Load();
        renamed.Products[0].DisplayName = "RenamedProduct";
        renamed.ActiveProduct = "RenamedProduct";
        store.Save(renamed);
        var reloaded = store.Load();

        var product = Assert.Single(reloaded.Products);
        Assert.Equal(id, product.Id);
        Assert.Equal("RenamedProduct", product.DisplayName);
        Assert.Equal(id, reloaded.ActiveProductId);
    }

    [Fact]
    public void EnabledInDisplayOrder_HidesDisabledAndAppliesSortOrder()
    {
        var products = new ProductDefinition[]
        {
            CreateProduct("Disabled", 0, isEnabled: false),
            CreateProduct("Second", 20),
            CreateProduct("First", 10),
        };

        var visible = ProductDefinitionValidator.EnabledInDisplayOrder(products);

        Assert.Equal(["First", "Second"], visible.Select(product => product.DisplayName));
    }

    [Fact]
    public void SaveAfterRemovingProduct_DoesNotDeleteFoldersOrPromptFile()
    {
        using var temp = new TempDirectory();
        var baseFolder = Path.Combine(temp.Path, "Product");
        var closedFolder = Path.Combine(baseFolder, "Closed");
        var promptPath = Path.Combine(temp.Path, "product.txt");
        Directory.CreateDirectory(closedFolder);
        File.WriteAllText(promptPath, "instructions", Encoding.UTF8);
        var store = new ConfigStore(Path.Combine(temp.Path, "config"));
        var settings = new UserSettings
        {
            Products =
            [
                new ProductProfile
                {
                    Id = Guid.NewGuid(),
                    DisplayName = "DisposableSetting",
                    BaseFolder = baseFolder,
                    ClosedFolder = closedFolder,
                    ProductPromptFilePath = promptPath,
                },
            ],
        };
        store.Save(settings);

        settings.Products.Clear();
        store.Save(settings);

        Assert.True(Directory.Exists(baseFolder));
        Assert.True(Directory.Exists(closedFolder));
        Assert.True(File.Exists(promptPath));
    }

    [Fact]
    public void ValidateAll_ReportsRequiredDuplicateAndInvalidPathErrors()
    {
        var products = new ProductDefinition[]
        {
            CreateProduct("Duplicate", 0),
            CreateProduct("duplicate", 1),
            new() { DisplayName = "", BaseFolder = "", ClosedFolder = "\0" },
        };

        var errors = ProductDefinitionValidator.ValidateAll(products);

        Assert.Contains(errors, error => error.Contains("製品表示名は必須", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("重複", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("パス形式が不正", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptLoader_LoadsCommonAndSelectedProductInstructions()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        var commonPath = Path.Combine(temp.Path, "prompts", "common.txt");
        var productPath = Path.Combine(temp.Path, "prompts", "products", "selected.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(productPath)!);
        File.WriteAllText(commonPath, "COMMON_RULE", Encoding.UTF8);
        File.WriteAllText(productPath, "SELECTED_PRODUCT_RULE", Encoding.UTF8);

        var result = SupportPromptFileLoader.Load(
            @"prompts\products\selected.txt",
            settingsPath,
            @"prompts\common.txt",
            temp.Path);

        Assert.Equal("COMMON_RULE", result.CommonInstruction);
        Assert.Equal("SELECTED_PRODUCT_RULE", result.ProductInstruction);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void PromptLoader_MissingProductInstructionWarnsWithoutThrowing()
    {
        using var temp = new TempDirectory();

        var result = SupportPromptFileLoader.Load(
            "missing-product.txt",
            Path.Combine(temp.Path, "user-settings.json"),
            "missing-common.txt",
            temp.Path);

        Assert.Empty(result.CommonInstruction);
        Assert.Empty(result.ProductInstruction);
        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, warning => Assert.Contains("見つかりません", warning, StringComparison.Ordinal));
    }

    private static ProductDefinition CreateProduct(string name, int sortOrder, bool isEnabled = true)
    {
        return new ProductDefinition
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            BaseFolder = $@"D:\Support\{name}",
            ClosedFolder = $@"D:\Support\{name}\Closed",
            IsEnabled = isEnabled,
            SortOrder = sortOrder,
        };
    }

    private static string CreateLegacyJson()
    {
        return """
            {
              "BaseFolder": "D:\\Support",
              "ActiveProduct": "HelixQAC",
              "Products": [
                { "Name": "HelixQAC", "BasePath": "D:\\Support\\QAC", "ClosedPath": "D:\\Support\\QAC\\Closed" },
                { "Name": "Checkmarx", "BasePath": "D:\\Support\\Checkmarx", "ClosedPath": "D:\\Support\\Checkmarx\\Closed" },
                { "Name": "Klcwork", "BasePath": "D:\\Support\\Klocwork", "ClosedPath": "D:\\Support\\Klocwork\\Closed" }
              ]
            }
            """;
    }
}
