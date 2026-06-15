namespace SupportCaseManager.Ai.Core.Diagnostics;

public interface IAiDiagnosticLogger
{
    Task LogInfoAsync(string message, CancellationToken cancellationToken = default);

    Task LogWarningAsync(string message, CancellationToken cancellationToken = default);

    Task LogErrorAsync(
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default);
}
