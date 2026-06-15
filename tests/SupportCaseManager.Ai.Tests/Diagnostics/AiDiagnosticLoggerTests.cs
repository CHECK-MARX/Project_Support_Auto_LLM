using SupportCaseManager.Ai.Core.Diagnostics;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Diagnostics;

public class AiDiagnosticLoggerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 2, 17, 15, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task LogInfoAsync_WritesInfoLogUnderAiDataLogs()
    {
        using var temp = new TempDirectory();
        var logger = CreateLogger(temp.Path);

        await logger.LogInfoAsync("Draft generation started.");

        var log = await ReadLogAsync(temp.Path);
        Assert.Contains("[INFO] Draft generation started.", log);
    }

    [Fact]
    public async Task LogWarningAsync_WritesWarnLog()
    {
        using var temp = new TempDirectory();
        var logger = CreateLogger(temp.Path);

        await logger.LogWarningAsync("Low confidence: 0.32");

        var log = await ReadLogAsync(temp.Path);
        Assert.Contains("[WARN] Low confidence: 0.32", log);
    }

    [Fact]
    public async Task LogErrorAsync_WritesErrorLogWithMinimalException()
    {
        using var temp = new TempDirectory();
        var logger = CreateLogger(temp.Path);

        await logger.LogErrorAsync("LLM response parse failed", new InvalidOperationException("Parse failed"));

        var log = await ReadLogAsync(temp.Path);
        Assert.Contains("[ERROR] LLM response parse failed: InvalidOperationException: Parse failed", log);
        Assert.DoesNotContain(" at ", log);
    }

    [Fact]
    public async Task LogInfoAsync_MasksEmailAddress()
    {
        using var temp = new TempDirectory();
        var logger = CreateLogger(temp.Path);

        await logger.LogInfoAsync("mail user@example.com");

        var log = await ReadLogAsync(temp.Path);
        Assert.DoesNotContain("user@example.com", log);
    }

    [Fact]
    public async Task LogInfoAsync_MasksPhoneNumber()
    {
        using var temp = new TempDirectory();
        var logger = CreateLogger(temp.Path);

        await logger.LogInfoAsync("phone 03-1234-5678");

        var log = await ReadLogAsync(temp.Path);
        Assert.DoesNotContain("03-1234-5678", log);
    }

    [Fact]
    public async Task LogInfoAsync_MasksApiKeyLikeText()
    {
        using var temp = new TempDirectory();
        var logger = CreateLogger(temp.Path);

        await logger.LogInfoAsync("api_key=sk-1234567890abcdef");

        var log = await ReadLogAsync(temp.Path);
        Assert.DoesNotContain("sk-1234567890abcdef", log);
    }

    [Fact]
    public async Task Logger_DoesNotCreateOrModifyExistingSupportCaseManagerLog()
    {
        using var temp = new TempDirectory();
        var existingLogsFolder = System.IO.Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(existingLogsFolder);
        var existingLogPath = System.IO.Path.Combine(existingLogsFolder, "SupportCaseManager.log");
        await File.WriteAllTextAsync(existingLogPath, "existing log");
        var expectedLastWriteTime = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(existingLogPath, expectedLastWriteTime);
        var logger = CreateLogger(temp.Path);

        await logger.LogInfoAsync("AI log only");

        Assert.Equal("existing log", await File.ReadAllTextAsync(existingLogPath));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(existingLogPath));
        Assert.True(File.Exists(System.IO.Path.Combine(temp.Path, "ai-data", "logs", "AiAssistant.log")));
    }

    private static AiDiagnosticLogger CreateLogger(string rootPath)
    {
        return new AiDiagnosticLogger(
            System.IO.Path.Combine(rootPath, "ai-data"),
            new SafetyRedactionService(),
            () => FixedNow);
    }

    private static Task<string> ReadLogAsync(string rootPath)
    {
        return File.ReadAllTextAsync(System.IO.Path.Combine(rootPath, "ai-data", "logs", "AiAssistant.log"));
    }
}
