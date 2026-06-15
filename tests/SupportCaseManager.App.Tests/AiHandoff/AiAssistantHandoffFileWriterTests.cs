using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.App.AiHandoff;
using SupportCaseManager.App.Tests.Helpers;

namespace SupportCaseManager.App.Tests.AiHandoff;

public class AiAssistantHandoffFileWriterTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 3, 19, 30, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task WriteAsync_WritesLaunchContextJsonUnderHandoffFolder()
    {
        using var temp = new TempDirectory();
        var handoffFolder = System.IO.Path.Combine(temp.Path, "ai-handoff");
        var writer = new AiAssistantHandoffFileWriter(handoffFolder, () => FixedNow);
        var context = CreateContext();

        var path = await writer.WriteAsync(context);

        Assert.True(File.Exists(path));
        Assert.Equal(handoffFolder, System.IO.Path.GetDirectoryName(path));
        Assert.Equal("ai-context-20260603-193000-00017581.json", System.IO.Path.GetFileName(path));

        await using var stream = File.OpenRead(path);
        var restored = await JsonSerializer.DeserializeAsync<AiAssistantLaunchContext>(stream);
        Assert.NotNull(restored);
        Assert.Equal("SupportCaseManager.App", restored.Source);
        Assert.Equal("Klocwork", restored.ProductName);
        Assert.Equal("株式会社サンプル", restored.CompanyName);
        Assert.Equal("ライセンス認証エラーです。", restored.CurrentNoteText);
    }

    [Fact]
    public async Task WriteAsync_CreatesHandoffFolderWhenMissing()
    {
        using var temp = new TempDirectory();
        var handoffFolder = System.IO.Path.Combine(temp.Path, "missing", "ai-handoff");
        var writer = new AiAssistantHandoffFileWriter(handoffFolder, () => FixedNow);

        _ = await writer.WriteAsync(CreateContext());

        Assert.True(Directory.Exists(handoffFolder));
    }

    [Fact]
    public async Task WriteAsync_UsesSafeFileNameForSupportNumber()
    {
        using var temp = new TempDirectory();
        var writer = new AiAssistantHandoffFileWriter(temp.Path, () => FixedNow);
        var context = CreateContext() with
        {
            SupportNumber = "00:01/75*81?",
        };

        var path = await writer.WriteAsync(context);
        var fileName = System.IO.Path.GetFileName(path);

        Assert.DoesNotContain(System.IO.Path.GetInvalidFileNameChars(), ch => fileName.Contains(ch));
        Assert.Equal("ai-context-20260603-193000-00_01_75_81.json", fileName);
    }

    [Fact]
    public async Task WriteAsync_DoesNotModifyExistingCaseFolderOrNote()
    {
        using var temp = new TempDirectory();
        var handoffFolder = System.IO.Path.Combine(temp.Path, "ai-handoff");
        var caseFolder = System.IO.Path.Combine(temp.Path, "case-folder");
        Directory.CreateDirectory(caseFolder);
        var notePath = System.IO.Path.Combine(caseFolder, "note.txt");
        await File.WriteAllTextAsync(notePath, "既存ノート");
        var expectedLastWriteTime = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(notePath, expectedLastWriteTime);

        var context = CreateContext() with
        {
            CaseFolderPath = caseFolder,
            NoteFilePath = notePath,
        };
        var writer = new AiAssistantHandoffFileWriter(handoffFolder, () => FixedNow);

        _ = await writer.WriteAsync(context);

        Assert.Equal("既存ノート", await File.ReadAllTextAsync(notePath));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(notePath));
        Assert.Single(Directory.GetFiles(caseFolder));
    }

    private static AiAssistantLaunchContext CreateContext()
    {
        return new AiAssistantLaunchContext
        {
            Source = "SupportCaseManager.App",
            ProductName = "Klocwork",
            BaseFolder = @"D:\Support\Open",
            CloseFolder = @"D:\Support\Closed",
            CaseFolderPath = @"D:\Support\Open\00017581",
            CompanyName = "株式会社サンプル",
            SupportNumber = "00017581",
            Status = "対応中",
            ReceptionDate = new DateOnly(2026, 6, 3),
            NoteKind = "お客様ご相談内容",
            NoteFilePath = @"D:\Support\Open\00017581\note.txt",
            SelectedText = "ライセンス認証エラー",
            CurrentNoteText = "ライセンス認証エラーです。",
            InquiryText = "ライセンス認証エラー",
        };
    }
}
