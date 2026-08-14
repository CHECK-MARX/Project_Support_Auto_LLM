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
        var contextPath = System.IO.Path.Combine(temp.Path, "ai-context-test.json");
        await File.WriteAllTextAsync(contextPath, "{}");
        var executablePath = System.IO.Path.Combine(temp.Path, AiAssistantExecutableResolver.ExecutableName);
        await File.WriteAllTextAsync(executablePath, string.Empty);
        var starter = new CapturingProcessStarter();
        var launcher = new AiAssistantProcessLauncher(new FixedExecutableResolver(executablePath), starter, temp.Path);

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
        using var temp = new TempDirectory();
        var starter = new CapturingProcessStarter();
        var launcher = new AiAssistantProcessLauncher(new FixedExecutableResolver("assistant.exe"), starter, temp.Path);

        await Assert.ThrowsAsync<FileNotFoundException>(() => launcher.LaunchAsync(
            System.IO.Path.Combine(temp.Path, "ai-context-missing.json")));

        Assert.Null(starter.StartInfo);
    }

    [Fact]
    public async Task LaunchAsync_ProcessExitsImmediatelyReportsFailure()
    {
        using var temp = new TempDirectory();
        var contextPath = System.IO.Path.Combine(temp.Path, "ai-context-test.json");
        await File.WriteAllTextAsync(contextPath, "{}");
        var starter = new CapturingProcessStarter(hasExited: true, exitCode: 17);
        var launcher = new AiAssistantProcessLauncher(new FixedExecutableResolver("assistant.exe"), starter, temp.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(contextPath));

        Assert.Contains("起動直後に終了", exception.Message, StringComparison.Ordinal);
        Assert.Contains("17", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_RejectsContextOutsideDedicatedHandoffFolder()
    {
        using var temp = new TempDirectory();
        var handoffFolder = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "handoff")).FullName;
        var outsidePath = System.IO.Path.Combine(temp.Path, "ai-context-outside.json");
        await File.WriteAllTextAsync(outsidePath, "{}");
        var starter = new CapturingProcessStarter();
        var launcher = new AiAssistantProcessLauncher(
            new FixedExecutableResolver("assistant.exe"),
            starter,
            handoffFolder);

        await Assert.ThrowsAsync<FileNotFoundException>(() => launcher.LaunchAsync(outsidePath));

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

    [Theory]
    [InlineData("Release")]
    [InlineData("Debug")]
    public void ExecutableResolver_PrefersAssistantMatchingMainBuildConfiguration(string configuration)
    {
        using var temp = new TempDirectory();
        var mainOutput = System.IO.Path.Combine(
            temp.Path, "src", "SupportCaseManager.App", "bin", configuration, "net10.0-windows");
        var releaseAssistant = System.IO.Path.Combine(
            temp.Path, "src", "SupportCaseManager.AiAssistant.App", "bin", "Release", "net10.0-windows",
            AiAssistantExecutableResolver.ExecutableName);
        var debugAssistant = System.IO.Path.Combine(
            temp.Path, "src", "SupportCaseManager.AiAssistant.App", "bin", "Debug", "net10.0-windows",
            AiAssistantExecutableResolver.ExecutableName);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(releaseAssistant)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(debugAssistant)!);
        File.WriteAllText(releaseAssistant, string.Empty);
        File.WriteAllText(debugAssistant, string.Empty);
        var resolver = new AiAssistantExecutableResolver(
            _ => null,
            File.Exists,
            appBaseDirectory: mainOutput,
            currentDirectory: temp.Path);

        var resolved = resolver.Resolve();

        var expected = string.Equals(configuration, "Release", StringComparison.Ordinal)
            ? releaseAssistant
            : debugAssistant;
        Assert.Equal(System.IO.Path.GetFullPath(expected), resolved);
    }

    private sealed class FixedExecutableResolver(string executablePath) : IAiAssistantExecutableResolver
    {
        public string Resolve() => executablePath;
    }

    private sealed class CapturingProcessStarter(bool hasExited = false, int exitCode = 0) : IProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public IStartedProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return new FakeStartedProcess(hasExited, exitCode);
        }
    }

    private sealed class FakeStartedProcess(bool hasExited, int exitCode) : IStartedProcess
    {
        public bool HasExited => hasExited;
        public int ExitCode => exitCode;
        public void Dispose()
        {
        }
    }
}
