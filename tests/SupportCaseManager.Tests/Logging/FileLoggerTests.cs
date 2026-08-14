using SupportCaseManager.Core.Logging;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Logging;

public sealed class FileLoggerTests
{
    [Fact]
    public void Info_EscapesLineBreaksAndControlCharacters()
    {
        using var temp = new TempDirectory();
        var logPath = Path.Combine(temp.Path, "app.log");
        var logger = new FileLogger(logPath, "category\r\nforged", alsoConsole: false);

        logger.Info("first\r\n[ERROR] forged\tvalue");

        var lines = File.ReadAllLines(logPath);
        Assert.Single(lines);
        Assert.Contains("category\\r\\nforged", lines[0]);
        Assert.Contains("first\\r\\n[ERROR] forged\\u0009value", lines[0]);
    }

    [Fact]
    public void Error_EscapesLineBreaksFromException()
    {
        using var temp = new TempDirectory();
        var logPath = Path.Combine(temp.Path, "app.log");
        var logger = new FileLogger(logPath, alsoConsole: false);

        logger.Error("failed", new InvalidOperationException("first\r\nforged"));

        var lines = File.ReadAllLines(logPath);
        Assert.Single(lines);
        Assert.Contains("first\\r\\nforged", lines[0]);
    }
}
