using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexDiagnosticLoggerTests
{
    [Fact]
    public async Task WriteAsync_RedactsEmailAndCredentials()
    {
        using var temp = new TempDirectory();
        using var logger = new CodexDiagnosticLogger(temp.Path);

        await logger.WriteAsync(
            "test",
            "contact=user@example.com key=sk-abcdefghijklmnopqrstuvwxyz Bearer abcdefghijklmnopqrstuvwxyz");

        var logPath = Assert.Single(Directory.GetFiles(logger.LogDirectory, "codex-*.log"));
        var content = await File.ReadAllTextAsync(logPath);
        Assert.DoesNotContain("user@example.com", content, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer abcdefghijklmnopqrstuvwxyz", content, StringComparison.Ordinal);
        Assert.Contains("[email-redacted]", content, StringComparison.Ordinal);
        Assert.Contains("[credential-redacted]", content, StringComparison.Ordinal);
    }
}
