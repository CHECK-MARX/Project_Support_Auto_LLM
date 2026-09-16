using System.Diagnostics;
using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookSearchServiceTests
{
    [Fact]
    public async Task EmptySupportId_DoesNothing()
    {
        var gateway = new FakeOutlookComGateway();
        var result = await new OutlookSearchService(gateway).SearchSupportIdAsync("   ");

        Assert.Equal(OutlookSearchStatus.Skipped, result.Status);
        Assert.Equal(0, gateway.GetRunningCount);
        Assert.Equal(0, gateway.StartCount);
        Assert.Equal(0, gateway.SearchCount);
    }

    [Fact]
    public async Task RunningOutlook_UsesExistingApplicationAndShowsCurrentStoreSearch()
    {
        var runningApplication = new object();
        var gateway = new FakeOutlookComGateway { RunningApplication = runningApplication };

        var result = await new OutlookSearchService(gateway).SearchSupportIdAsync(" 00018303 ");

        Assert.Equal(OutlookSearchStatus.Succeeded, result.Status);
        Assert.False(result.OutlookStarted);
        Assert.Equal(0, gateway.StartCount);
        Assert.Same(runningApplication, gateway.SearchedApplication);
        Assert.Equal("00018303", gateway.Query);
    }

    [Fact]
    public async Task OutlookNotRunning_StartsApplicationAndShowsSearch()
    {
        var startedApplication = new object();
        var gateway = new FakeOutlookComGateway { StartedApplication = startedApplication };

        var result = await new OutlookSearchService(gateway).SearchSupportIdAsync("00018303");

        Assert.Equal(OutlookSearchStatus.Succeeded, result.Status);
        Assert.True(result.OutlookStarted);
        Assert.Equal(1, gateway.StartCount);
        Assert.Same(startedApplication, gateway.SearchedApplication);
    }

    [Fact]
    public async Task ComFailure_ReturnsConciseFailureWithoutThrowing()
    {
        var gateway = new FakeOutlookComGateway { Failure = new InvalidOperationException("COM detail") };

        var result = await new OutlookSearchService(gateway).SearchSupportIdAsync("00018303");

        Assert.Equal(OutlookSearchStatus.Failed, result.Status);
        Assert.Equal("Classic Outlookで検索を開始できませんでした。", result.Message);
    }

    [Fact]
    public async Task ConcurrentDoubleClick_ReturnsBusyWithoutBlockingCaller()
    {
        using var releaseSearch = new ManualResetEventSlim();
        var gateway = new FakeOutlookComGateway { SearchBlocker = releaseSearch };
        var service = new OutlookSearchService(gateway);
        var first = service.SearchSupportIdAsync("00018303");
        Assert.True(gateway.SearchStarted.Wait(TimeSpan.FromSeconds(2)));

        var stopwatch = Stopwatch.StartNew();
        var second = await service.SearchSupportIdAsync("00018303");
        stopwatch.Stop();

        Assert.Equal(OutlookSearchStatus.Busy, second.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        releaseSearch.Set();
        Assert.Equal(OutlookSearchStatus.Succeeded, (await first).Status);
        Assert.Equal(1, gateway.SearchCount);
    }

    private sealed class FakeOutlookComGateway : IOutlookComGateway
    {
        public object? RunningApplication { get; init; }
        public object StartedApplication { get; init; } = new();
        public Exception? Failure { get; init; }
        public ManualResetEventSlim? SearchBlocker { get; init; }
        public ManualResetEventSlim SearchStarted { get; } = new();
        public int GetRunningCount { get; private set; }
        public int StartCount { get; private set; }
        public int SearchCount { get; private set; }
        public object? SearchedApplication { get; private set; }
        public string Query { get; private set; } = string.Empty;

        public object? TryGetRunningApplication()
        {
            GetRunningCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return RunningApplication;
        }

        public object StartApplication()
        {
            StartCount++;
            return StartedApplication;
        }

        public void ShowCurrentStoreSearch(object application, string query)
        {
            SearchCount++;
            SearchedApplication = application;
            Query = query;
            SearchStarted.Set();
            SearchBlocker?.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
