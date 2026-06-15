using System.Diagnostics;
using SupportCaseManager.App.AiHandoff;
using SupportCaseManager.App.Tests.Helpers;

namespace SupportCaseManager.App.Tests.AiHandoff;

public class AiAssistantProcessLauncherTests
{
    [Fact]
    public async Task LaunchAsync_StartsAiAssistantWithContextFileArgument()
    {
        using var temp = new TempDirectory();
        var contextPath = System.IO.Path.Combine(temp.Path, "context.json");
        await File.WriteAllTextAsync(contextPath, "{}");
        var executablePath = System.IO.Path.Combine(temp.Path, AiAssistantExecutableResolver.ExecutableName);
        await File.WriteAllTextAsync(executablePath, string.Empty);
        var starter = new CapturingProcessStarter();
        var launcher = new AiAssistantProcessLauncher(new FixedExecutableResolver(executablePath), starter);

        await launcher.LaunchAsync(contextPath);

        Assert.NotNull(starter.StartInfo);
        Assert.Equal(executablePath, starter.StartInfo.FileName);
        Assert.False(starter.StartInfo.UseShellExecute);
        Assert.Equal(temp.Path, starter.StartInfo.WorkingDirectory);
        Assert.Equal(["--context-file", contextPath], starter.StartInfo.ArgumentList.ToArray());
    }

    [Fact]
    public async Task LaunchAsync_MissingContextFileDoesNotStartProcess()
    {
        var starter = new CapturingProcessStarter();
        var launcher = new AiAssistantProcessLauncher(new FixedExecutableResolver("assistant.exe"), starter);

        await Assert.ThrowsAsync<FileNotFoundException>(() => launcher.LaunchAsync(@"C:\missing\context.json"));

        Assert.Null(starter.StartInfo);
    }

    [Fact]
    public void ExecutableResolver_UsesEnvironmentVariableFirst()
    {
        using var temp = new TempDirectory();
        var envPath = System.IO.Path.Combine(temp.Path, AiAssistantExecutableResolver.ExecutableName);
        File.WriteAllText(envPath, string.Empty);
        var sameFolderPath = System.IO.Path.Combine(temp.Path, "same", AiAssistantExecutableResolver.ExecutableName);
        var resolver = new AiAssistantExecutableResolver(
            name => name == AiAssistantExecutableResolver.EnvironmentVariableName ? $" \"{envPath}\" " : null,
            File.Exists,
            appBaseDirectory: System.IO.Path.GetDirectoryName(sameFolderPath),
            currentDirectory: temp.Path);

        var resolved = resolver.Resolve();

        Assert.Equal(System.IO.Path.GetFullPath(envPath), resolved);
    }

    [Fact]
    public void ExecutableResolver_UsesSameFolderWhenEnvironmentVariableIsEmpty()
    {
        using var temp = new TempDirectory();
        var sameFolder = System.IO.Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(sameFolder);
        var sameFolderPath = System.IO.Path.Combine(sameFolder, AiAssistantExecutableResolver.ExecutableName);
        File.WriteAllText(sameFolderPath, string.Empty);
        var resolver = new AiAssistantExecutableResolver(
            _ => null,
            File.Exists,
            appBaseDirectory: sameFolder,
            currentDirectory: temp.Path);

        var resolved = resolver.Resolve();

        Assert.Equal(System.IO.Path.GetFullPath(sameFolderPath), resolved);
    }

    [Fact]
    public void ExecutableResolver_InvalidEnvironmentVariableThrowsClearError()
    {
        var resolver = new AiAssistantExecutableResolver(
            name => name == AiAssistantExecutableResolver.EnvironmentVariableName ? @"C:\missing\assistant.exe" : null,
            _ => false,
            appBaseDirectory: @"C:\app",
            currentDirectory: @"C:\repo");

        var exception = Assert.Throws<FileNotFoundException>(() => resolver.Resolve());

        Assert.Contains(AiAssistantExecutableResolver.EnvironmentVariableName, exception.Message);
    }

    private sealed class FixedExecutableResolver(string executablePath) : IAiAssistantExecutableResolver
    {
        public string Resolve() => executablePath;
    }

    private sealed class CapturingProcessStarter : IProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public void Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
        }
    }
}
