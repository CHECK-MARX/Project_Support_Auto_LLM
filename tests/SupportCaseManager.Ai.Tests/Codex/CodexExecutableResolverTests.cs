using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexExecutableResolverTests
{
    [Fact]
    public async Task ResolveAsync_PrefersConfiguredPath()
    {
        var configured = Path.GetFullPath("configured-codex.exe");
        var resolver = new CodexExecutableResolver(
            fileExists: path => path == configured,
            whereProvider: (_, _) => Task.FromResult<IReadOnlyList<string>>(["path-codex.exe"]));

        var result = await resolver.ResolveAsync(configured);

        Assert.True(result.Found);
        Assert.Equal(configured, result.ExecutablePath);
        Assert.Equal(CodexExecutableSource.UserSetting, result.Source);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToWhereThenStandardLocation()
    {
        var wherePath = Path.GetFullPath("where-codex.exe");
        var resolver = new CodexExecutableResolver(
            fileExists: path => path == wherePath,
            localAppDataProvider: () => Path.GetFullPath("local"),
            whereProvider: (_, _) => Task.FromResult<IReadOnlyList<string>>([wherePath]));

        var result = await resolver.ResolveAsync("missing.exe");

        Assert.Equal(CodexExecutableSource.PathEnvironment, result.Source);
        Assert.Equal(wherePath, result.ExecutablePath);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsJapaneseGuidanceWhenNotFound()
    {
        var resolver = new CodexExecutableResolver(
            fileExists: _ => false,
            whereProvider: (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

        var result = await resolver.ResolveAsync(null);

        Assert.False(result.Found);
        Assert.Equal(CodexExecutableSource.NotFound, result.Source);
        Assert.Contains("Codex実行ファイル", result.Message);
    }

    [Fact]
    public void CreateStartInfo_UsesRequiredStdioAndArguments()
    {
        var startInfo = CodexAppServerProcessHost.CreateStartInfo("codex.exe");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(["app-server", "--stdio"], startInfo.ArgumentList);
        Assert.Equal("utf-8", startInfo.StandardInputEncoding?.WebName);
    }
}
