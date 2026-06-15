using System.Text;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Notes;

public class NoteSnapshotReaderTests
{
    [Fact]
    public async Task ReadAllAsync_ReadsTxtNotesInCaseFolder()
    {
        using var temp = new TempDirectory();
        var notePath = System.IO.Path.Combine(temp.Path, "お客様ご相談内容_00001234.txt");
        await File.WriteAllTextAsync(notePath, "日本語のノート本文", Encoding.UTF8);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "ignored.md"), "対象外", Encoding.UTF8);
        var reader = new NoteSnapshotReader();

        var notes = await reader.ReadAllAsync(temp.Path);

        Assert.Single(notes);
        Assert.Equal("お客様ご相談内容", notes[0].NoteKind);
        Assert.Equal("日本語のノート本文", notes[0].Text);
        Assert.Equal(notePath, notes[0].FilePath);
    }

    [Theory]
    [InlineData("お客様ご相談内容_00001234.txt", "お客様ご相談内容")]
    [InlineData("お客様への返信案_00001234.txt", "お客様への返信案")]
    [InlineData("メーカー連携内容_00001234.txt", "メーカー連携内容")]
    [InlineData("custom-note.txt", "Unknown")]
    public void DetectNoteKind_DetectsJapaneseNoteKinds(string fileName, string expected)
    {
        var actual = NoteSnapshotReader.DetectNoteKind(fileName);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadAsync_PreservesJapaneseTextAndWindowsPathText()
    {
        using var temp = new TempDirectory();
        var notePath = System.IO.Path.Combine(temp.Path, "お客様への返信案_00001234.txt");
        var text = "Windowsパス: D:\\Support\\Cases\\00001234\r\n本文: 日本語";
        await File.WriteAllTextAsync(notePath, text, Encoding.UTF8);
        var reader = new NoteSnapshotReader();

        var note = await reader.ReadAsync(notePath);

        Assert.NotNull(note);
        Assert.Equal(text, note.Text);
        Assert.Equal("お客様への返信案", note.NoteKind);
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsEmptyListForEmptyFolder()
    {
        using var temp = new TempDirectory();
        var reader = new NoteSnapshotReader();

        var notes = await reader.ReadAllAsync(temp.Path);

        Assert.Empty(notes);
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsEmptyListForMissingFolder()
    {
        using var temp = new TempDirectory();
        var missingFolder = System.IO.Path.Combine(temp.Path, "missing");
        var reader = new NoteSnapshotReader();

        var notes = await reader.ReadAllAsync(missingFolder);

        Assert.Empty(notes);
    }

    [Fact]
    public async Task ReadAsync_DoesNotChangeLastWriteTime()
    {
        using var temp = new TempDirectory();
        var notePath = System.IO.Path.Combine(temp.Path, "メーカー連携内容_00001234.txt");
        await File.WriteAllTextAsync(notePath, "読み取り専用テスト", Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(notePath, expectedLastWriteTime);
        var reader = new NoteSnapshotReader();

        _ = await reader.ReadAsync(notePath);

        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(notePath));
    }
}
