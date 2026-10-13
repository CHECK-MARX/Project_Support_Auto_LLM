using SupportCaseManager.App.Outlook;
using SupportCaseManager.Core.Config;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookAcceptanceMonitorTests
{
    private static readonly DateTime Enabled = new(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly OutlookAcceptanceCandidate Candidate = new(
        "mail-key", "entry", "store", "body-hash", Enabled.AddMinutes(2), "00018952",
        "株式会社シーイーシー", "Eio Uchida", "eiouchi@cec-ltd.co.jp", "Klocwork inquiry",
        "2026/09/26 18:00", "Question", "Sender/From");

    [Fact]
    public async Task FirstEnableDoesNotBackfillAndRestartCatchesStoppedPeriod()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(OutlookAcceptanceMonitorTests), Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigStore(root);
            var gateway = new FakeGateway { Candidates = [Candidate] };
            var monitor = new OutlookAcceptanceMonitor(config, config.Load(), gateway);
            monitor.Enable(Enabled);
            var first = await monitor.ScanAsync(Enabled.AddMinutes(3), CancellationToken.None);
            Assert.Equal(Enabled, gateway.LastSince);
            Assert.Single(first);
            monitor.CompleteScan(Enabled.AddMinutes(40));

            var restarted = new OutlookAcceptanceMonitor(config, config.Load(), gateway);
            restarted.Enable(Enabled.AddDays(1));
            await restarted.ScanAsync(Enabled.AddDays(1), CancellationToken.None);
            Assert.Equal(Enabled, restarted.Diagnostics.EnabledAt);
            Assert.Equal(Enabled.AddMinutes(10), gateway.LastSince);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RejectedMailIsNotShownAgainButSupportIdIsNotBlacklisted()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(OutlookAcceptanceMonitorTests), Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigStore(root);
            var gateway = new FakeGateway { Candidates = [Candidate] };
            var monitor = new OutlookAcceptanceMonitor(config, config.Load(), gateway);
            monitor.Enable(Enabled);
            Assert.Single(await monitor.ScanAsync(Enabled.AddMinutes(3), CancellationToken.None));
            monitor.MarkProcessed(Candidate, rejected: true);
            var restarted = new OutlookAcceptanceMonitor(config, config.Load(), gateway);
            Assert.Empty(await restarted.ScanAsync(Enabled.AddMinutes(4), CancellationToken.None));
            gateway.Candidates = [Candidate with { MailKey = "another-mail" }];
            Assert.Single(await restarted.ScanAsync(Enabled.AddMinutes(5), CancellationToken.None));
            Assert.Equal(1, restarted.Diagnostics.Rejected);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DryRunDoesNotChangeSettingsOrMarkMail()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(OutlookAcceptanceMonitorTests), Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigStore(root);
            var gateway = new FakeGateway { Candidates = [Candidate] };
            var monitor = new OutlookAcceptanceMonitor(config, config.Load(), gateway);
            monitor.Enable(Enabled);
            var before = File.ReadAllBytes(config.SettingsPath);
            Assert.Single(await monitor.DryRunAsync(Enabled.AddDays(-1), CancellationToken.None));
            Assert.Equal(before, File.ReadAllBytes(config.SettingsPath));
            Assert.Equal(0, monitor.Diagnostics.Processed);
            Assert.Equal(0, monitor.Diagnostics.Rejected);
            Assert.Single(await monitor.ScanAsync(Enabled.AddMinutes(3), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SameMailAndSameLogicalCopyAreDeduplicated()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(OutlookAcceptanceMonitorTests), Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigStore(root);
            var gateway = new FakeGateway { Candidates = [Candidate, Candidate with { MailKey = "other-copy" }] };
            var monitor = new OutlookAcceptanceMonitor(config, config.Load(), gateway);
            monitor.Enable(Enabled);
            Assert.Equal(2, (await monitor.ScanAsync(Enabled.AddMinutes(3), CancellationToken.None)).Count);
            monitor.MarkProcessed(Candidate, rejected: false);
            Assert.Empty(await monitor.ScanAsync(Enabled.AddMinutes(4), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SelfAddressedInboxAndSentCopiesQueueOnceAndRejectOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(OutlookAcceptanceMonitorTests), Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigStore(root);
            var body = """
                本件は、ID: 00999999 で受け付けました。
                ------------ Original Message ------------
                送信者: Test User <test@example.com>
                送信: 2026/09/26 00:50
                件名: Outlook受付検知テスト

                本文です。
                """;
            var inbox = new OutlookAcceptanceEnvelope("inbox-entry", "store", Enabled.AddMinutes(2),
                "Checkmarxサポートについて", body, "itoke@toyo.co.jp", ["itoke@toyo.co.jp"], [])
            { InternetMessageId = "<self-test@example.com>" };
            var sent = inbox with { EntryId = "sent-entry" };
            var first = Assert.IsType<OutlookAcceptanceCandidate>(OutlookAcceptanceMail.Parse(inbox));
            var second = Assert.IsType<OutlookAcceptanceCandidate>(OutlookAcceptanceMail.Parse(sent));
            var gateway = new FakeGateway { Candidates = [first, second] };
            var monitor = new OutlookAcceptanceMonitor(config, config.Load(), gateway);
            monitor.Enable(Enabled);
            var queued = Assert.Single(await monitor.ScanAsync(Enabled.AddMinutes(3), CancellationToken.None));
            Assert.Equal("00999999", queued.SupportId);
            monitor.MarkProcessed(queued, rejected: true);
            Assert.Empty(await monitor.ScanAsync(Enabled.AddMinutes(4), CancellationToken.None));
            Assert.Equal(1, monitor.Diagnostics.Rejected);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeGateway : IOutlookAcceptanceGateway
    {
        public IReadOnlyList<OutlookAcceptanceCandidate> Candidates { get; set; } = [];
        public DateTime LastSince { get; private set; }
        public IReadOnlyList<OutlookAcceptanceCandidate> Scan(DateTime sinceUtc, CancellationToken token)
        {
            LastSince = sinceUtc;
            return Candidates;
        }
        public bool Revalidate(OutlookAcceptanceCandidate candidate) => true;
    }
}
