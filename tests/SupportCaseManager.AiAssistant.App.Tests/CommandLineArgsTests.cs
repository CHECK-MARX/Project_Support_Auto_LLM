using SupportCaseManager.AiAssistant.App.Launch;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class CommandLineArgsTests
{
    [Fact]
    public void Parse_SupportsLongContextFileOption()
    {
        var parser = new CommandLineArgsParser();

        var options = parser.Parse(["--context-file", @"C:\Temp\ai-context.json"]);

        Assert.Equal(@"C:\Temp\ai-context.json", options.ContextFilePath);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Parse_SupportsShortContextFileOption()
    {
        var parser = new CommandLineArgsParser();

        var options = parser.Parse(["-c", @"C:\Temp\ai-context.json"]);

        Assert.Equal(@"C:\Temp\ai-context.json", options.ContextFilePath);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Parse_NoArgumentsStartsWithoutContext()
    {
        var parser = new CommandLineArgsParser();

        var options = parser.Parse([]);

        Assert.Null(options.ContextFilePath);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Parse_UnsupportedArgumentsAreIgnoredSafely()
    {
        var parser = new CommandLineArgsParser();

        var options = parser.Parse(["--unknown", "value", "--context-file", @"C:\Temp\ai-context.json"]);

        Assert.Equal(@"C:\Temp\ai-context.json", options.ContextFilePath);
        Assert.Equal(2, options.Warnings.Count);
        Assert.All(options.Warnings, warning => Assert.DoesNotContain("value", warning));
    }

    [Fact]
    public void Parse_MissingContextPathDoesNotThrow()
    {
        var parser = new CommandLineArgsParser();

        var options = parser.Parse(["--context-file"]);

        Assert.Null(options.ContextFilePath);
        Assert.Single(options.Warnings);
        Assert.Contains("requires", options.Warnings[0]);
    }
}
