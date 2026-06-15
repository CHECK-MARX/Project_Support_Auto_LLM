using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using SupportCaseManager.Core.Compatibility;

namespace SupportCaseManager.Core.Config;

public sealed class ConfigStore
{
    private readonly string _configDir;
    private readonly string _path;

    public ConfigStore(string? configDir = null)
    {
        var baseDir = AppContext.BaseDirectory;
        var defaultDir = Path.Combine(baseDir, "config");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localDir = Path.Combine(local, "itoke", "SupportCaseManager");
        var forcePortable = string.Equals(
            Environment.GetEnvironmentVariable("SUPPORT_CASE_MANAGER_PORTABLE"),
            "1",
            StringComparison.Ordinal);
        var portableMarker = Path.Combine(defaultDir, "portable.mode");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            _configDir = configDir;
        }
        else if (forcePortable || File.Exists(portableMarker))
        {
            _configDir = defaultDir;
        }
        else
        {
            _configDir = localDir;
        }

        Directory.CreateDirectory(_configDir);
        _path = Path.Combine(_configDir, "user-settings.json");

        // 旧バージョンで EXE 配下 config を使っていた場合、
        // LocalAppData に設定が無ければ一度だけ移行する。
        if (string.IsNullOrWhiteSpace(configDir) && !forcePortable && !File.Exists(portableMarker))
        {
            TryMigrateLegacySettings(defaultDir, _path);
        }
    }

    public string SettingsPath => _path;

    public UserSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new UserSettings();
        }

        try
        {
            var json = File.ReadAllText(_path, EncodingPolicy.Utf8NoBom);
            using var doc = JsonDocument.Parse(json);
            return ParseSettings(doc.RootElement);
        }
        catch (Exception)
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        var recent = settings.RecentCases.Take(Defaults.MaxRecentCases).ToList();
        settings.RecentCases = recent;

        var payload = new Dictionary<string, object?>
        {
            ["BaseFolder"] = settings.BasePath,
            ["DarkMode"] = settings.DarkMode,
            ["WindowGeometry"] = settings.WindowGeometry,
            ["SplitterState"] = settings.SplitterState,
            ["RecentCases"] = recent,
            ["Statuses"] = settings.Statuses?.Count > 0 ? settings.Statuses : Defaults.DefaultStatuses,
            ["NoteTemplates"] = settings.NoteTemplates ?? new List<Dictionary<string, string>>(),
            ["Products"] = settings.Products?
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.BasePath))
                .Select(item => new Dictionary<string, object?>
                {
                    ["Name"] = item.Name,
                    ["BasePath"] = item.BasePath,
                    ["ClosedPath"] = item.ClosedPath ?? string.Empty,
                    ["NoteTemplates"] = SerializeTemplates(item.NoteTemplates ?? new List<Dictionary<string, string>>()),
                })
                .ToList() ?? new List<Dictionary<string, object?>>(),
            ["ActiveProduct"] = settings.ActiveProduct,
            ["ExcludedCases"] = settings.ExcludedCases ?? new List<string>(),
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var json = JsonSerializer.Serialize(payload, options);
        File.WriteAllText(_path, json, EncodingPolicy.Utf8NoBom);
    }

    public void AddRecentCase(UserSettings settings, string folderPath)
    {
        var trimmed = folderPath?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        var existing = settings.RecentCases
            .Where(item => !string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        existing.Insert(0, trimmed);
        settings.RecentCases = existing.Take(Defaults.MaxRecentCases).ToList();
        Save(settings);
    }

    private static UserSettings ParseSettings(JsonElement root)
    {
        var settings = new UserSettings
        {
            BasePath = ReadString(root, "BaseFolder") ?? ReadString(root, "BasePath") ?? string.Empty,
            DarkMode = ReadBool(root, "DarkMode") ?? true,
            WindowGeometry = ReadIntList(root, "WindowGeometry"),
            SplitterState = ReadIntList(root, "SplitterState"),
            RecentCases = ReadStringList(root, "RecentCases"),
            Statuses = ReadStringList(root, "Statuses"),
            NoteTemplates = ReadTemplateList(root, "NoteTemplates"),
            Products = ReadProductList(root, "Products"),
            ActiveProduct = ReadString(root, "ActiveProduct") ?? string.Empty,
            ExcludedCases = ReadStringList(root, "ExcludedCases"),
        };

        if (settings.Statuses.Count == 0)
        {
            settings.Statuses = Defaults.DefaultStatuses.ToList();
        }

        return settings;
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool? ReadBool(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    private static List<int> ReadIntList(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<int>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                {
                    list.Add(number);
                }
            }

            return list;
        }

        return new List<int>();
    }

    private static List<string> ReadStringList(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (text != null)
                    {
                        list.Add(text);
                    }
                }
            }

            return list;
        }

        return new List<string>();
    }

    private static List<ProductProfile> ReadProductList(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<ProductProfile>();
        }

        var list = new List<ProductProfile>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadObjectString(item, "Name") ?? ReadObjectString(item, "name");
            var basePath = ReadObjectString(item, "BasePath")
                ?? ReadObjectString(item, "BaseFolder")
                ?? ReadObjectString(item, "basePath")
                ?? ReadObjectString(item, "baseFolder");
            var closedPath = ReadObjectString(item, "ClosedPath")
                ?? ReadObjectString(item, "ClosedFolder")
                ?? ReadObjectString(item, "CloseFolder")
                ?? ReadObjectString(item, "closePath")
                ?? ReadObjectString(item, "closeFolder");
            var templates = ReadTemplateList(item, "NoteTemplates");

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }

            list.Add(new ProductProfile
            {
                Name = name,
                BasePath = basePath,
                ClosedPath = closedPath ?? string.Empty,
                NoteTemplates = templates,
            });
        }

        return list;
    }

    private static string? ReadObjectString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static List<Dictionary<string, string>> ReadTemplateList(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<Dictionary<string, string>>();
        }

        var list = new List<Dictionary<string, string>>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var dict = new Dictionary<string, string>();
            foreach (var entry in item.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    dict[entry.Name] = entry.Value.GetString() ?? string.Empty;
                }
            }

            list.Add(dict);
        }

        return list;
    }

    private static List<Dictionary<string, string>> SerializeTemplates(IEnumerable<Dictionary<string, string>> templates)
    {
        var list = new List<Dictionary<string, string>>();
        foreach (var template in templates)
        {
            if (!template.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!template.TryGetValue("text", out var text))
            {
                text = string.Empty;
            }

            list.Add(new Dictionary<string, string>
            {
                ["name"] = name,
                ["text"] = text,
            });
        }

        return list;
    }

    private static void TryMigrateLegacySettings(string legacyConfigDir, string localSettingsPath)
    {
        try
        {
            if (File.Exists(localSettingsPath))
            {
                return;
            }

            var legacyPath = Path.Combine(legacyConfigDir, "user-settings.json");
            if (!File.Exists(legacyPath))
            {
                return;
            }

            var localDir = Path.GetDirectoryName(localSettingsPath);
            if (!string.IsNullOrWhiteSpace(localDir))
            {
                Directory.CreateDirectory(localDir);
            }

            File.Copy(legacyPath, localSettingsPath, overwrite: false);
        }
        catch
        {
            // ignore migration failures and continue with defaults
        }
    }
}
