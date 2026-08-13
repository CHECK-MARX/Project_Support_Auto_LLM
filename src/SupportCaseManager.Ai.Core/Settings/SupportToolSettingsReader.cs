using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Core.Compatibility;

namespace SupportCaseManager.Ai.Core.Settings;

public sealed class SupportToolSettingsReader : ISupportToolSettingsReader
{
    public string? FindDefaultSettingsFilePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config", "user-settings.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "itoke",
                "SupportCaseManager",
                "user-settings.json"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<SupportToolProductSettings>> ReadProductsAsync(
        string userSettingsFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userSettingsFilePath))
        {
            throw new ArgumentException("Support tool settings file path is required.", nameof(userSettingsFilePath));
        }

        if (!File.Exists(userSettingsFilePath))
        {
            throw new FileNotFoundException("Support tool user-settings.json was not found.", userSettingsFilePath);
        }

        var bytes = await File.ReadAllBytesAsync(userSettingsFilePath, cancellationToken);
        var json = EncodingPolicy.DecodeNoteText(bytes);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var products = ReadProducts(root);

        return products
            .Where(static item => !string.IsNullOrWhiteSpace(item.ProductName))
            .GroupBy(static item => item.Id == Guid.Empty ? item.ProductName.Trim() : item.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.SortOrder)
            .ToList();
    }

    private static IReadOnlyList<SupportToolProductSettings> ReadProducts(JsonElement root)
    {
        var products = new List<SupportToolProductSettings>();
        if (TryGetProperty(root, "Products", out var productsElement)
            && productsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in productsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var productName = ReadString(item, "DisplayName", "displayName", "Name", "ProductName", "productName", "name");
                var baseFolder = ReadString(item, "BasePath", "BaseFolder", "basePath", "baseFolder");
                var closeFolder = ReadString(item, "ClosedPath", "CloseFolder", "ClosedFolder", "closedPath", "closeFolder", "closedFolder");
                if (string.IsNullOrWhiteSpace(productName))
                {
                    continue;
                }

                products.Add(new SupportToolProductSettings
                {
                    Id = ReadGuid(item, "Id", "id"),
                    ProductName = productName.Trim(),
                    Aliases = ReadStringList(item, "Aliases", "aliases"),
                    BaseFolder = baseFolder?.Trim() ?? string.Empty,
                    CloseFolder = closeFolder?.Trim() ?? string.Empty,
                    ProductPromptFilePath = ReadString(item, "ProductPromptFilePath", "productPromptFilePath")?.Trim() ?? string.Empty,
                    IsEnabled = ReadBool(item, "IsEnabled", "isEnabled") ?? true,
                    SortOrder = ReadInt(item, "SortOrder", "sortOrder") ?? products.Count,
                });
            }
        }

        if (products.Count > 0)
        {
            return products;
        }

        var rootProductName = ReadString(root, "ActiveProduct", "ProductName", "DefaultProductName", "Name");
        var rootBaseFolder = ReadString(root, "BasePath", "BaseFolder", "basePath", "baseFolder");
        var rootCloseFolder = ReadString(root, "ClosedPath", "CloseFolder", "ClosedFolder", "closedPath", "closeFolder", "closedFolder");
        if (!string.IsNullOrWhiteSpace(rootProductName)
            || !string.IsNullOrWhiteSpace(rootBaseFolder)
            || !string.IsNullOrWhiteSpace(rootCloseFolder))
        {
            products.Add(new SupportToolProductSettings
            {
                ProductName = string.IsNullOrWhiteSpace(rootProductName) ? "Default" : rootProductName.Trim(),
                BaseFolder = rootBaseFolder?.Trim() ?? string.Empty,
                CloseFolder = rootCloseFolder?.Trim() ?? string.Empty,
            });
        }

        return products;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static Guid ReadGuid(JsonElement element, params string[] names)
    {
        var text = ReadString(element, names);
        return Guid.TryParse(text, out var value) ? value : Guid.Empty;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.EnumerateArray()
                .Where(static value => value.ValueKind == JsonValueKind.String)
                .Select(static value => value.GetString()?.Trim() ?? string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    private static bool? ReadBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property) && property.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
