using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.App.AiHandoff;

public sealed class AiAssistantHandoffFileWriter : IAiAssistantHandoffFileWriter
{
    private readonly string handoffFolder;
    private readonly Func<DateTimeOffset> nowProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public AiAssistantHandoffFileWriter(
        string? handoffFolder = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.handoffFolder = string.IsNullOrWhiteSpace(handoffFolder)
            ? AiAssistantHandoffPathPolicy.DefaultFolder
            : handoffFolder;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public async Task<string> WriteAsync(
        AiAssistantLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!AiAssistantHandoffPathPolicy.TryNormalizeRoot(
                handoffFolder,
                createIfMissing: true,
                out var normalizedRoot))
        {
            throw new InvalidOperationException("AI回答支援の引き渡しフォルダを安全に使用できません。");
        }

        var fileName = CreateFileName(context.SupportNumber, nowProvider());
        var candidate = Path.Combine(normalizedRoot, fileName);
        if (!AiAssistantHandoffPathPolicy.TryNormalizeContextFile(
                candidate,
                normalizedRoot,
                requireExisting: false,
                out var path))
        {
            throw new InvalidOperationException("AI回答支援の引き渡しファイルパスが不正です。");
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, context, JsonOptions, cancellationToken);
        return path;
    }

    private static string CreateFileName(string? supportNumber, DateTimeOffset now)
    {
        var safeSupportNumber = SafeFileNameComponent(supportNumber);
        return $"ai-context-{now:yyyyMMdd-HHmmss}-{safeSupportNumber}.json";
    }

    private static string SafeFileNameComponent(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = normalized
            .Select(ch => invalidChars.Contains(ch) || char.IsControl(ch) ? '_' : ch)
            .ToArray();
        var safe = new string(chars).Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }
}
