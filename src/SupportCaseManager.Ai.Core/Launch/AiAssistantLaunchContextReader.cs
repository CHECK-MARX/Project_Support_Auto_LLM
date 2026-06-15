using System.Text.Json;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Launch;

public sealed class AiAssistantLaunchContextReader : IAiAssistantLaunchContextReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AiAssistantLaunchContext> ReadAsync(
        string contextFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contextFilePath))
        {
            throw new ArgumentException("Context file path is required.", nameof(contextFilePath));
        }

        if (!File.Exists(contextFilePath))
        {
            throw new FileNotFoundException("AI assistant context file was not found.", contextFilePath);
        }

        await using var stream = File.Open(contextFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var context = await JsonSerializer.DeserializeAsync<AiAssistantLaunchContext>(
            stream,
            JsonOptions,
            cancellationToken);

        return context ?? throw new JsonException("AI assistant context JSON was empty or invalid.");
    }
}
