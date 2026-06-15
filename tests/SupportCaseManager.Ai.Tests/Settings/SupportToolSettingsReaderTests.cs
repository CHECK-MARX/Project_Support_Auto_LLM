using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Settings;

public sealed class SupportToolSettingsReaderTests
{
    [Fact]
    public async Task ReadProductsAsync_ReadsSupportToolUserSettings()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "BaseFolder": "D:\\Support\\Root",
              "Products": [
                {
                  "Name": "HelixQAC",
                  "BasePath": "D:\\Support\\HelixQAC",
                  "ClosedPath": "D:\\Support\\HelixQAC\\Closed",
                  "Ignored": "value"
                }
              ],
              "ActiveProduct": "HelixQAC"
            }
            """,
            Encoding.UTF8);
        var reader = new SupportToolSettingsReader();

        var products = await reader.ReadProductsAsync(settingsPath);

        var product = Assert.Single(products);
        Assert.Equal("HelixQAC", product.ProductName);
        Assert.Equal(@"D:\Support\HelixQAC", product.BaseFolder);
        Assert.Equal(@"D:\Support\HelixQAC\Closed", product.CloseFolder);
    }

    [Fact]
    public async Task ReadProductsAsync_ToleratesAlternatePropertyNames()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "products": [
                {
                  "productName": "Checkmarx",
                  "baseFolder": "D:\\Support\\Checkmarx",
                  "closeFolder": "D:\\Support\\Checkmarx\\Closed"
                }
              ]
            }
            """,
            Encoding.UTF8);
        var reader = new SupportToolSettingsReader();

        var products = await reader.ReadProductsAsync(settingsPath);

        var product = Assert.Single(products);
        Assert.Equal("Checkmarx", product.ProductName);
        Assert.Equal(@"D:\Support\Checkmarx", product.BaseFolder);
        Assert.Equal(@"D:\Support\Checkmarx\Closed", product.CloseFolder);
    }

    [Fact]
    public async Task ReadProductsAsync_MissingFileFailsClearly()
    {
        using var temp = new TempDirectory();
        var reader = new SupportToolSettingsReader();

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            reader.ReadProductsAsync(Path.Combine(temp.Path, "missing-user-settings.json")));

        Assert.Contains("user-settings.json", exception.Message);
    }

    [Fact]
    public async Task ReadProductsAsync_DoesNotModifyUserSettingsFile()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        var json = """
            {
              "Products": [
                { "Name": "Klocwork", "BasePath": "D:\\Support\\Klocwork", "ClosedPath": "D:\\Support\\Klocwork\\Closed" }
              ]
            }
            """;
        await File.WriteAllTextAsync(settingsPath, json, Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(settingsPath, expectedLastWriteTime);
        var reader = new SupportToolSettingsReader();

        _ = await reader.ReadProductsAsync(settingsPath);

        Assert.Equal(json, await File.ReadAllTextAsync(settingsPath, Encoding.UTF8));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(settingsPath));
    }

    [Fact]
    public async Task ReadProductsAsync_InvalidJsonFailsClearly()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "user-settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ invalid json", Encoding.UTF8);
        var reader = new SupportToolSettingsReader();

        await Assert.ThrowsAnyAsync<JsonException>(() => reader.ReadProductsAsync(settingsPath));
    }
}
