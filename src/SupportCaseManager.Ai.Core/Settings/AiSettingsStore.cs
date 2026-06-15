using System.Text.Json;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Settings;

public sealed class AiSettingsStore : IAiSettingsStore
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<AiAssistantSettings> LoadAsync(
        string aiDataFolder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(aiDataFolder))
        {
            throw new ArgumentException("AI data folder is required.", nameof(aiDataFolder));
        }

        var settingsPath = ResolveSettingsPath(aiDataFolder);
        if (!File.Exists(settingsPath))
        {
            return new AiAssistantSettings
            {
                AiDataFolder = aiDataFolder,
            };
        }

        await using var stream = File.OpenRead(settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AiAssistantSettings>(
            stream,
            JsonOptions,
            cancellationToken);

        settings ??= new AiAssistantSettings();
        return string.IsNullOrWhiteSpace(settings.AiDataFolder)
            ? settings with { AiDataFolder = aiDataFolder }
            : settings;
    }

    public async Task SaveAsync(
        AiAssistantSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.AiDataFolder))
        {
            throw new ArgumentException("AI data folder is required.", nameof(settings));
        }

        Directory.CreateDirectory(settings.AiDataFolder);

        var settingsPath = ResolveSettingsPath(settings.AiDataFolder);
        await using var stream = File.Create(settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    public static string ResolveSettingsPath(string aiDataFolder)
    {
        return Path.Combine(aiDataFolder, SettingsFileName);
    }
}
