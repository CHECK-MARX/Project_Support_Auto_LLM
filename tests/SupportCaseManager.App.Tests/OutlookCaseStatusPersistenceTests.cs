using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookCaseStatusPersistenceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "OutlookStatusPersistenceTests", Guid.NewGuid().ToString("N"));
    private readonly OutlookCaseKey key = new("Checkmarx", "00012345", @"C:\cases\one");
    private OutlookCaseStatusStore Store => new(Path.Combine(folder, "statuses.json"));

    [Fact]
    public async Task RestartShowsLastSuccessfulStatusBeforeQueryAndWhileOutlookIsClosed()
    {
        var mail = new OutlookMailReference("entry", "store", DateTime.Now.AddMinutes(-20), "Case 00012345", "support@checkmarx.com", "", key.SupportId, true);
        var status = OutlookCaseMailClassifier.Build(key, new(new HashSet<string>(), new HashSet<string>()), [new(mail, true, [mail.Sender])]);
        var online = new Gateway(status);
        var first = new OutlookCaseStatusService(online, Store);
        var saved = (await first.RefreshAsync([key], false))[key];
        var offline = new Gateway(OutlookCaseMailStatus.Message("Outlook未起動"));
        var restarted = new OutlookCaseStatusService(offline, Store);

        Assert.True(restarted.TryGetCachedStatus(key, out var restored));
        Assert.Equal(0, offline.ReadCount);
        Assert.Equal(saved.Primary, restored.Primary);
        Assert.Equal(saved.CheckedAtUtc, restored.CheckedAtUtc);
        Assert.Contains("未再確認", restored.RefreshError);
        var unavailable = (await restarted.RefreshAsync([key], false))[key];
        Assert.Equal(1, offline.ReadCount); // Persisted cache must not suppress startup's live check.
        Assert.Equal("MANUFACTURER_NEEDS_REPLY", unavailable.WorkflowState);
        Assert.Equal(mail, unavailable.Primary.Mail);
        Assert.Equal(saved.CheckedAtUtc, unavailable.CheckedAtUtc);
        Assert.Equal("Outlook未起動", unavailable.RefreshError);

        var nextRestart = new OutlookCaseStatusService(offline, Store);
        Assert.True(nextRestart.TryGetCachedStatus(key, out var stillSaved));
        Assert.Equal(saved.Primary, stillSaved.Primary);
        Assert.False(nextRestart.TryGetCachedStatus(key with { Product = "Other" }, out _));
        Assert.False(nextRestart.TryGetCachedStatus(key with { FolderPath = @"C:\cases\two" }, out _));
        Assert.False(nextRestart.TryGetCachedStatus(key with { SupportId = "00012346" }, out _));
    }

    [Fact]
    public void InformationalDisplayAndSourceReferenceSurviveRestart()
    {
        var reference = new OutlookMailReference("entry", "store", DateTime.Now, "Case 00012345", "maker@example.com", "", key.SupportId, true);
        var progress = OutlookCaseMailClassifier.Build(key, new(new HashSet<string>(), new HashSet<string> { reference.Sender }),
            [new(reference, true, [reference.Sender])
            {
                ManufacturerMessageKind = OutlookManufacturerMessageKind.ProgressOnly,
                ManufacturerMessageSummary = "メーカー内引継ぎ（進捗連絡・回答待ち）"
            }]) with { CheckedAtUtc = DateTime.UtcNow };
        Assert.True(Store.Save(new Dictionary<OutlookCaseKey, OutlookCaseMailStatus> { [key] = progress }));
        var restarted = new OutlookCaseStatusService(new Gateway(OutlookCaseMailStatus.Message("Outlook未起動")), Store);
        Assert.True(restarted.TryGetCachedStatus(key, out var restored));
        Assert.True(restored.Primary.IsInformational);
        Assert.Equal(reference, restored.Primary.Mail);
        Assert.Equal("WAITING_MANUFACTURER_REPLY", restored.WorkflowState);
    }

    [Fact]
    public async Task FreshSuccessfulReadReplacesPersistedStatusAndClearsOfflineWarning()
    {
        var old = OutlookCaseMailStatus.Message("old") with { WorkflowState = "NO_MAIL", CheckedAtUtc = DateTime.UtcNow.AddDays(-3) };
        Assert.True(Store.Save(new Dictionary<OutlookCaseKey, OutlookCaseMailStatus> { [key] = old }));
        var gateway = new Gateway(old with { Primary = new("メールなし"), CheckedAtUtc = null });
        var service = new OutlookCaseStatusService(gateway, Store);
        var fresh = (await service.RefreshAsync([key], false))[key];
        Assert.Equal(string.Empty, fresh.RefreshError);
        Assert.True(fresh.CheckedAtUtc > old.CheckedAtUtc);
        Assert.Equal("メールなし", Store.Load()[key].Primary.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{truncated")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Entries\":[]}")]
    public void MissingOrMalformedCacheDoesNotPreventStartup(string text)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "statuses.json"), text);
        Assert.Empty(Store.Load());
        Assert.False(new OutlookCaseStatusService(new Gateway(OutlookCaseMailStatus.Message("offline")), Store).TryGetCachedStatus(key, out _));
    }

    [Fact]
    public void UnavailableResultsAreNotPersistedAndWriteFailureKeepsPreviousFile()
    {
        Assert.True(Store.Save(new Dictionary<OutlookCaseKey, OutlookCaseMailStatus> { [key] = OutlookCaseMailStatus.Message("Outlook未起動") }));
        Assert.Empty(Store.Load());
        var status = OutlookCaseMailStatus.Message("メールなし") with { WorkflowState = "NO_MAIL", CheckedAtUtc = DateTime.UtcNow };
        Assert.True(Store.Save(new Dictionary<OutlookCaseKey, OutlookCaseMailStatus> { [key] = status }));
        using (File.Open(Path.Combine(folder, "statuses.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(Store.Save(new Dictionary<OutlookCaseKey, OutlookCaseMailStatus>()));
        Assert.Equal(status.Primary, Store.Load()[key].Primary);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }

    private sealed class Gateway(OutlookCaseMailStatus status) : IOutlookCaseStatusGateway
    {
        public int ReadCount { get; private set; }
        public IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus> ReadCases(IReadOnlyList<OutlookCaseKey> cases, CancellationToken token)
        {
            ReadCount++;
            return cases.ToDictionary(static key => key, _ => status);
        }
        public bool OpenMail(OutlookCaseKey key, OutlookMailReference reference, CancellationToken token) => false;
    }
}
