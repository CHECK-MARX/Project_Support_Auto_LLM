using System.Text;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Cases;

public class CaseContextBuilderTests
{
    [Fact]
    public async Task BuildFromCaseFolderAsync_ParsesStandardFolderName()
    {
        using var temp = new TempDirectory();
        var caseFolder = System.IO.Path.Combine(temp.Path, "20260602(株式会社サンプル_00001234)対応中_20260602");
        Directory.CreateDirectory(caseFolder);
        var builder = new CaseContextBuilder(new NoteSnapshotReader());

        var context = await builder.BuildFromCaseFolderAsync(caseFolder);

        Assert.Equal("SupportCaseManager.Ai.Core", context.Source);
        Assert.Equal(caseFolder, context.CaseFolderPath);
        Assert.Equal("株式会社サンプル", context.CompanyName);
        Assert.Equal("00001234", context.SupportNumber);
        Assert.Equal("対応中", context.Status);
        Assert.Equal(new DateOnly(2026, 6, 2), context.ReceptionDate);
    }

    [Fact]
    public async Task BuildFromCaseFolderAsync_IncludesNoteSnapshots()
    {
        using var temp = new TempDirectory();
        var caseFolder = System.IO.Path.Combine(temp.Path, "20260602(ABC_00001234)調査中_20260602");
        Directory.CreateDirectory(caseFolder);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(caseFolder, "お客様ご相談内容_00001234.txt"),
            "問い合わせ本文",
            Encoding.UTF8);
        var builder = new CaseContextBuilder(new NoteSnapshotReader());

        var context = await builder.BuildFromCaseFolderAsync(caseFolder);

        Assert.Single(context.Notes);
        Assert.Equal("お客様ご相談内容", context.Notes[0].NoteKind);
        Assert.Equal("問い合わせ本文", context.Notes[0].Text);
    }

    [Fact]
    public async Task BuildFromCaseFolderAsync_AppliesProvidedProductAndFolders()
    {
        using var temp = new TempDirectory();
        var baseFolder = System.IO.Path.Combine(temp.Path, "base");
        var closeFolder = System.IO.Path.Combine(temp.Path, "closed");
        var caseFolder = System.IO.Path.Combine(baseFolder, "20260602(ABC_00001234)調査中_20260602");
        Directory.CreateDirectory(caseFolder);
        var builder = new CaseContextBuilder(new NoteSnapshotReader());

        var context = await builder.BuildFromCaseFolderAsync(
            caseFolder,
            productName: "製品A",
            baseFolder: baseFolder,
            closeFolder: closeFolder);

        Assert.Equal("製品A", context.ProductName);
        Assert.Equal(baseFolder, context.BaseFolder);
        Assert.Equal(closeFolder, context.CloseFolder);
    }

    [Fact]
    public async Task BuildFromCaseFolderAsync_DoesNotModifyFiles()
    {
        using var temp = new TempDirectory();
        var caseFolder = System.IO.Path.Combine(temp.Path, "20260602(ABC_00001234)調査中_20260602");
        Directory.CreateDirectory(caseFolder);
        var notePath = System.IO.Path.Combine(caseFolder, "お客様ご相談内容_00001234.txt");
        await File.WriteAllTextAsync(notePath, "問い合わせ本文", Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(notePath, expectedLastWriteTime);
        var builder = new CaseContextBuilder(new NoteSnapshotReader());

        _ = await builder.BuildFromCaseFolderAsync(caseFolder);

        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(notePath));
        Assert.Equal("問い合わせ本文", await File.ReadAllTextAsync(notePath));
    }

    [Fact]
    public async Task BuildFromCaseFolderAsync_DoesNotCreateExistingAppDataFiles()
    {
        using var temp = new TempDirectory();
        var caseFolder = System.IO.Path.Combine(temp.Path, "20260602(ABC_00001234)調査中_20260602");
        Directory.CreateDirectory(caseFolder);
        var builder = new CaseContextBuilder(new NoteSnapshotReader());

        _ = await builder.BuildFromCaseFolderAsync(caseFolder);

        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "cases-index.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "user-settings.json")));
    }
}
