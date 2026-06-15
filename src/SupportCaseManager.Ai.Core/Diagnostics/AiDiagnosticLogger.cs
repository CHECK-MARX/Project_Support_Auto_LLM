using System.Globalization;
using System.Text;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Core.Diagnostics;

public sealed class AiDiagnosticLogger : IAiDiagnosticLogger
{
    private const string LogsFolderName = "logs";
    private const string LogFileName = "AiAssistant.log";

    private readonly string aiDataFolder;
    private readonly ISafetyRedactionService safetyRedactionService;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public AiDiagnosticLogger(
        string aiDataFolder,
        ISafetyRedactionService? safetyRedactionService = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        if (string.IsNullOrWhiteSpace(aiDataFolder))
        {
            throw new ArgumentException("AI data folder is required.", nameof(aiDataFolder));
        }

        this.aiDataFolder = aiDataFolder;
        this.safetyRedactionService = safetyRedactionService ?? new SafetyRedactionService();
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public Task LogInfoAsync(string message, CancellationToken cancellationToken = default)
    {
        return WriteAsync("INFO", message, null, cancellationToken);
    }

    public Task LogWarningAsync(string message, CancellationToken cancellationToken = default)
    {
        return WriteAsync("WARN", message, null, cancellationToken);
    }

    public Task LogErrorAsync(
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        return WriteAsync("ERROR", message, exception, cancellationToken);
    }

    private async Task WriteAsync(
        string level,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        var logsFolder = Path.Combine(aiDataFolder, LogsFolderName);
        Directory.CreateDirectory(logsFolder);

        var logPath = Path.Combine(logsFolder, LogFileName);
        var safeMessage = safetyRedactionService.RedactForLog(message ?? string.Empty);
        if (exception is not null)
        {
            var safeExceptionMessage = safetyRedactionService.RedactForLog(exception.Message);
            safeMessage = $"{safeMessage}: {exception.GetType().Name}: {safeExceptionMessage}";
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{nowProvider():O} [{level}] {safeMessage}{Environment.NewLine}");

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(logPath, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
