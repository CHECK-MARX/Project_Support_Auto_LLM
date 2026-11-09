using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookCaseStatusChangeTests
{
    private static readonly OutlookCaseKey Old = new("Checkmarx", "00012345", @"C:\cases\20261105(Test_00012345)調査中");
    private static readonly OutlookCaseKey New = Old with { FolderPath = @"C:\cases\20261109(Test_00012345)メーカへ確認中" };
    private static readonly OutlookCaseKey Other = new("Klocwork", "00056789", @"C:\other\case");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusEditForcesOnlyTheChangedCaseEvenWithinCacheLifetime(bool samePath)
    {
        var gateway = new Gateway();
        var service = new OutlookCaseStatusService(gateway);
        await service.RefreshAsync([Old, Other], false);
        service.TryGetCachedStatus(Other, out var unchanged);
        gateway.Result = Status("reply observed");
        var current = samePath ? Old : New;
        var updated = await service.RefreshAfterStatusChangeAsync(Old, current);
        Assert.Equal("reply observed", updated.Primary.Text);
        Assert.Equal(2, gateway.Calls.Count);
        Assert.Equal([current], gateway.Calls.Last());
        Assert.True(service.TryGetCachedStatus(Other, out var stillUnchanged));
        Assert.Same(unchanged, stillUnchanged);
        await service.RefreshAsync([current, Other], false);
        Assert.Equal(2, gateway.Calls.Count);
    }

    [Fact]
    public async Task FolderRenamePreservesLastKnownResultWhileRefreshingAndIfOutlookIsClosed()
    {
        var gateway = new Gateway();
        var service = new OutlookCaseStatusService(gateway);
        var before = (await service.RefreshAsync([Old], false))[Old];
        gateway.Result = OutlookCaseMailStatus.Message("Outlook未起動");
        gateway.Started.Reset();
        gateway.Release.Reset();
        var pending = service.RefreshAfterStatusChangeAsync(Old, New);
        Assert.True(gateway.Started.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(service.TryGetCachedStatus(New, out var during));
        Assert.Equal(before.Primary, during.Primary);
        Assert.Contains("再確認中", during.RefreshError);
        gateway.Release.Set();
        var offline = await pending;
        Assert.Equal(before.Primary, offline.Primary);
        Assert.Equal(before.CheckedAtUtc, offline.CheckedAtUtc);
        Assert.Equal("Outlook未起動", offline.RefreshError);
    }

    [Fact]
    public async Task TargetedReadQueuesBehindActiveScanAndItsFreshResultWins()
    {
        var gateway = new Gateway();
        var service = new OutlookCaseStatusService(gateway);
        await service.RefreshAsync([Old, Other], false);
        gateway.Started.Reset();
        gateway.Release.Reset();
        gateway.Result = Status("earlier scan");
        var scan = service.RefreshAsync([Old, Other], true);
        Assert.True(gateway.Started.Wait(TimeSpan.FromSeconds(3)));
        gateway.Result = Status("latest reply");
        var changed = service.RefreshAfterStatusChangeAsync(Old, New);
        Assert.Equal(2, gateway.Calls.Count);
        gateway.Release.Set();
        await scan;
        Assert.Equal("latest reply", (await changed).Primary.Text);
        Assert.Equal([New], gateway.Calls.Last());
        Assert.True(service.TryGetCachedStatus(New, out var result));
        Assert.Equal("latest reply", result.Primary.Text);
    }

    [Fact]
    public async Task OtherProductOrSupportIdCannotInheritOldCaseState()
    {
        var gateway = new Gateway();
        var service = new OutlookCaseStatusService(gateway);
        await service.RefreshAsync([Old], false);
        foreach (var current in new[] { New with { Product = "Klocwork" }, New with { SupportId = "00099999" } })
        {
            gateway.Started.Reset();
            gateway.Release.Reset();
            var task = service.RefreshAfterStatusChangeAsync(Old, current);
            Assert.True(gateway.Started.Wait(TimeSpan.FromSeconds(3)));
            Assert.False(service.TryGetCachedStatus(current, out _));
            gateway.Release.Set();
            await task;
        }
    }

    [Fact]
    public async Task StatusEditOfJustClosedCaseAlsoRefreshesWithoutOpenList()
    {
        var gateway = new Gateway();
        var service = new OutlookCaseStatusService(gateway);
        var closed = New with { FolderPath = @"C:\closed\2026\20261109(Test_00012345)クローズ" };
        await service.RefreshAfterStatusChangeAsync(Old, closed);
        Assert.Equal([closed], gateway.Calls.Single());
        Assert.True(service.TryGetCachedStatus(closed, out _));
    }

    [Fact]
    public async Task EmptySupportIdCannotStartOutlookQuery()
    {
        var gateway = new Gateway();
        var result = await new OutlookCaseStatusService(gateway).RefreshAfterStatusChangeAsync(Old, New with { SupportId = "" });
        Assert.Equal("UNAVAILABLE", result.WorkflowState);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public void BothSuccessfulStatusEditRoutesScheduleTargetedRefreshOutsideVisibilityGuard()
    {
        var source = File.ReadAllText(SourcePath());
        foreach (var name in new[] { "private bool TryUpdateCaseStatus(", "private void OnStatusUpdate(" })
        {
            var body = Method(source, name);
            Assert.Contains("RefreshChangedCaseOutlookAsync(", body);
            Assert.True(body.IndexOf("Directory.Move(", StringComparison.Ordinal) < body.IndexOf("RefreshChangedCaseOutlookAsync(", StringComparison.Ordinal));
        }
        var refresh = Method(source, "private async Task RefreshChangedCaseOutlookAsync(");
        Assert.Contains("RefreshAfterStatusChangeAsync(", refresh);
        Assert.DoesNotContain("IsStatusTabVisible", refresh);
        Assert.Contains("_openCases", refresh);
        Assert.Contains("_staleCases", refresh);
    }

    private static string Method(string source, string name)
    {
        var start = source.IndexOf(name, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("\n    private ", start + name.Length, StringComparison.Ordinal);
        return source[start..end];
    }
    private static string SourcePath([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "SupportCaseManager.App", "MainWindow.xaml.cs"));
    private static OutlookCaseMailStatus Status(string text) => OutlookCaseMailStatus.Message(text) with { WorkflowState = "CUSTOMER_REPLIED" };
    private sealed class Gateway : IOutlookCaseStatusGateway
    {
        public ConcurrentQueue<OutlookCaseKey[]> Calls { get; } = new();
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new(true);
        public OutlookCaseMailStatus Result { get; set; } = Status("previous read");
        public IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus> ReadCases(IReadOnlyList<OutlookCaseKey> keys, CancellationToken token)
        {
            var result = Result;
            Calls.Enqueue(keys.ToArray());
            Started.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5), token)) throw new TimeoutException();
            return keys.ToDictionary(static key => key, _ => result);
        }
        public bool OpenMail(OutlookCaseKey key, OutlookMailReference reference, CancellationToken token) => false;
    }
}
