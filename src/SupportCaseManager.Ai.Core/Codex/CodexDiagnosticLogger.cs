using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Codex;

public interface ICodexDiagnosticLogger
{
    string LogDirectory { get; }
    Task WriteAsync(string category, string message, Exception? exception = null, CancellationToken cancellationToken = default);
}

public sealed class CodexDiagnosticLogger : ICodexDiagnosticLogger, IDisposable
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private bool disposed;

    public CodexDiagnosticLogger(string? localApplicationData = null)
    {
        var root = string.IsNullOrWhiteSpace(localApplicationData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationData;
        LogDirectory = Path.Combine(root, "itoke", "SupportCaseManager", "logs");
    }

    public string LogDirectory { get; }

    public async Task WriteAsync(
        string category,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var safeCategory = Normalize(category, 40);
        var safeMessage = Normalize(message, 500);
        var safeException = exception is null
            ? string.Empty
            : $" exception={exception.GetType().Name}: {Normalize(exception.Message, 300)}";
        var line = $"{DateTimeOffset.Now:O} [{safeCategory}] {safeMessage}{safeException}{Environment.NewLine}";
        var path = Path.Combine(LogDirectory, $"codex-{DateTime.Now:yyyyMMdd}.log");

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(LogDirectory);
            await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Diagnostics must never stop the support workflow.
        }
        finally
        {
            writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writeGate.Dispose();
    }

    private static string Normalize(string? value, int maxLength)
    {
        var text = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        text = Regex.Replace(text, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[email-redacted]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:sk-[A-Za-z0-9_-]{12,}|Bearer\s+[A-Za-z0-9._-]{12,})\b", "[credential-redacted]", RegexOptions.IgnoreCase);
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
