using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.Launch;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class LaunchContextCaseFolderResolverTests
{
    [Fact]
    public void Resolve_WhenHandoffUsesAnotherProductsPath_FindsCaseUnderConfiguredProductRoot()
    {
        using var temp = new TempDirectory();
        var baseFolder = Path.Combine(temp.Path, "CHECKMARX");
        var actualCaseFolder = Path.Combine(baseFolder, "20260722(三菱重工業株式会社_00018249)受付_20260722");
        Directory.CreateDirectory(actualCaseFolder);
        var noteFile = Path.Combine(actualCaseFolder, "お客様ご相談内容_00018249.txt");
        File.WriteAllText(noteFile, "問い合わせ");

        var context = new AiAssistantLaunchContext
        {
            ProductName = "Checkmarx",
            BaseFolder = baseFolder,
            CloseFolder = Path.Combine(baseFolder, "クローズ案件"),
            CaseFolderPath = Path.Combine(temp.Path, "QAC", "00018249"),
            NoteFilePath = Path.Combine(temp.Path, "QAC", "00018249", Path.GetFileName(noteFile)),
            SupportNumber = "00018249",
            CompanyName = "三菱重工業株式会社",
            ReceptionDate = new DateOnly(2026, 7, 22),
            Status = "受付",
        };

        var resolved = LaunchContextCaseFolderResolver.Resolve(context);

        Assert.Equal(actualCaseFolder, resolved.CaseFolderPath);
        Assert.Equal(noteFile, resolved.NoteFilePath);
    }

    [Fact]
    public void Resolve_WhenNoMatchingCaseExists_PreservesOriginalContext()
    {
        using var temp = new TempDirectory();
        var context = new AiAssistantLaunchContext
        {
            BaseFolder = temp.Path,
            CaseFolderPath = Path.Combine(temp.Path, "missing"),
            SupportNumber = "00019999",
        };

        var resolved = LaunchContextCaseFolderResolver.Resolve(context);

        Assert.Same(context, resolved);
        Assert.Equal(context.CaseFolderPath, resolved.CaseFolderPath);
    }

    [Fact]
    public void Resolve_WhenCaseWasMovedToClosedFolder_FollowsTheSupportNumber()
    {
        using var temp = new TempDirectory();
        var baseFolder = Path.Combine(temp.Path, "base");
        var closedFolder = Path.Combine(temp.Path, "closed");
        var movedCaseFolder = Path.Combine(closedFolder, "20260710(株式会社デンソー_00018126)クローズ_20260722");
        Directory.CreateDirectory(baseFolder);
        Directory.CreateDirectory(movedCaseFolder);
        var context = new AiAssistantLaunchContext
        {
            BaseFolder = baseFolder,
            CloseFolder = closedFolder,
            CaseFolderPath = Path.Combine(baseFolder, "20260710(株式会社デンソー_00018126)受付_20260710"),
            SupportNumber = "00018126",
            CompanyName = "株式会社デンソー",
            ReceptionDate = new DateOnly(2026, 7, 10),
            Status = "クローズ",
        };

        var resolved = LaunchContextCaseFolderResolver.Resolve(context);

        Assert.Equal(movedCaseFolder, resolved.CaseFolderPath);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                nameof(LaunchContextCaseFolderResolverTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
