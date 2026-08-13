using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexSessionAndReviewTests
{
    [Fact]
    public async Task SessionStore_SavesRestoresAndUpdatesThreadByCase()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "codex-sessions.json");
        var store = new CodexSessionStore(file);
        var productId = Guid.NewGuid();
        var session = new CodexSession
        {
            SupportId = "0001",
            ProductId = productId,
            CaseFolder = temp.Path,
            CodexThreadId = "thread-1",
            CreatedAt = DateTimeOffset.Now,
            LastUsedAt = DateTimeOffset.Now,
            Messages = [new CodexSessionMessage { Role = "assistant", Text = "answer", CreatedAt = DateTimeOffset.Now }],
        };

        await store.SaveAsync(session);
        var restored = await store.FindAsync("0001", productId, temp.Path);
        await store.SaveAsync(session with { CodexThreadId = "thread-2", LastUsedAt = DateTimeOffset.Now.AddMinutes(1) });
        var updated = await store.LoadAsync();

        Assert.Equal("thread-1", restored?.CodexThreadId);
        Assert.Equal("answer", restored?.Messages.Single().Text);
        Assert.Single(updated.Sessions);
        Assert.Equal("thread-2", updated.Sessions.Single().CodexThreadId);
    }

    [Fact]
    public async Task SessionStore_CorruptFileDoesNotCrash()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "codex-sessions.json");
        File.WriteAllText(file, "{broken");

        var result = await new CodexSessionStore(file).LoadAsync();

        Assert.Empty(result.Sessions);
        Assert.Contains("読み込めません", result.Warning);
    }

    [Fact]
    public async Task SessionStore_FollowsCaseWhenFolderNameChangesWithoutDuplicatingHistory()
    {
        using var temp = new TempDirectory();
        var store = new CodexSessionStore(Path.Combine(temp.Path, "codex-sessions.json"));
        var productId = Guid.NewGuid();
        var oldFolder = Path.Combine(temp.Path, "00018250_受付");
        var movedFolder = Path.Combine(temp.Path, "00018250_メーカー確認中");
        var original = new CodexSession
        {
            SupportId = "00018250",
            ProductId = productId,
            CaseFolder = oldFolder,
            CodexThreadId = "thread-before-move",
            LastUsedAt = DateTimeOffset.Now,
            Messages = [new CodexSessionMessage { Role = "assistant", Text = "保存済み回答", CreatedAt = DateTimeOffset.Now }],
        };
        await store.SaveAsync(original);

        var foundAfterMove = await store.FindAsync("00018250", productId, movedFolder);
        await store.SaveAsync(foundAfterMove! with { CaseFolder = movedFolder, CodexThreadId = "thread-after-move" });
        var saved = await store.LoadAsync();

        Assert.Equal("thread-before-move", foundAfterMove?.CodexThreadId);
        Assert.Single(saved.Sessions);
        Assert.Equal(movedFolder, saved.Sessions.Single().CaseFolder);
        Assert.Equal("thread-after-move", saved.Sessions.Single().CodexThreadId);
        Assert.Equal("保存済み回答", saved.Sessions.Single().Messages.Single().Text);
    }

    [Fact]
    public void TechnicalDiff_DetectsChangedVersionHotfixCommandAndUrl()
    {
        var before = "Version 12.0 HF-3 command `qacli sync` https://example.test/v12";
        var after = "Version 12.1 HF-4 command `qacli upload` https://example.test/v13";

        var result = new CodexTechnicalValueDiffDetector().Compare(before, after);

        Assert.True(result.HasDifferences);
        Assert.Contains(result.AddedValues, value => value.Contains("12.1"));
        Assert.Contains(result.RemovedValues, value => value.Contains("HF-3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("qacli upload", result.AddedValues);
    }
}
