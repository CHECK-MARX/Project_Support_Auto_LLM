using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookCaseStatusGatewayTests
{
    private static readonly OutlookCaseKey Key = new("Checkmarx", "00018925", "unused");

    [Fact]
    public void OutlookNotRunningDoesNotStartIt()
    {
        var com = new StubOutlookComGateway();
        var result = new OutlookCaseStatusGateway(com).ReadCases([Key], CancellationToken.None);
        Assert.Equal("Outlook未起動", result[Key].Primary.Text);
        Assert.Equal(0, com.StartCount);
    }

    [Fact]
    public void IndexedSearchUsesInboxSentAndSubfoldersAndReturnsExactMail()
    {
        var app = FakeApplication.Create();
        var com = new StubOutlookComGateway { Running = app };
        var gateway = new OutlookCaseStatusGateway(com);
        var result = gateway.ReadCases([Key], CancellationToken.None);

        Assert.Equal(0, com.StartCount);
        Assert.Equal("UNCLASSIFIED", result[Key].WorkflowState);
        Assert.Equal("child", result[Key].Primary.Mail?.EntryId);
        Assert.All(app.AllFolders, folder => Assert.StartsWith("@SQL=", folder.LastFilter));
        Assert.True(app.Inbox.GetTableCalls > 0);
        Assert.True(app.Sent.GetTableCalls > 0);
        Assert.True(app.Child.GetTableCalls > 0);
    }

    [Fact]
    public void StaleEntryIdRequeriesButWrongCaseNeverOpens()
    {
        var app = FakeApplication.Create();
        var gateway = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app });
        var mail = gateway.ReadCases([Key], CancellationToken.None)[Key].Primary.Mail!;

        Assert.False(gateway.OpenMail(Key, mail with { SupportId = "00018926" }, CancellationToken.None));
        Assert.False(app.ChildMail.WasDisplayed);
        Assert.True(gateway.OpenMail(Key, mail with { EntryId = "stale" }, CancellationToken.None));
        Assert.True(app.ChildMail.WasDisplayed);
    }

    [Fact]
    public void Case18195SelfSentInboxCopyBeatsLaterSalesforceNotification()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseStatusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "お客様ご相談内容_00018195.txt"),
                string.Concat(Enumerable.Repeat("Inquiry line\n", 15)) + "MAIL：imura-k@mail.dnp.co.jp\n");
            var key = new OutlookCaseKey("Checkmarx", "00018195", folder);
            var app = FakeApplication.CreateRegression();
            var result = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app })
                .ReadCases([key], CancellationToken.None)[key];

            Assert.Equal("CUSTOMER_REPLIED", result.WorkflowState);
            Assert.EndsWith("15:24 お客様へ返信済み", result.Primary.Text, StringComparison.Ordinal);
            Assert.False(result.Primary.Mail?.IsInbound);
            Assert.Equal("sent-copy", result.Primary.Mail?.EntryId);
            Assert.Null(result.LatestUnclassified);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Case18952SalesforceNoticeAloneDoesNotBecomeInbound()
    {
        var app = FakeApplication.Create();
        app.Inbox.Entries.Add(("notice", FakeApplication.SalesforceNotice("00018952", new DateTime(2026, 9, 25, 16, 58, 0))));
        var key = new OutlookCaseKey("Klocwork", "00018952", "unused");
        var result = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app })
            .ReadCases([key], CancellationToken.None)[key];
        Assert.Equal("NO_MAIL", result.WorkflowState);
        Assert.Null(result.LatestCustomerInbound);
        Assert.Null(result.LatestUnclassified);
    }

    [Fact]
    public void Case18952LaterSalesforceNoticeDoesNotHideGenuineOutbound()
    {
        var app = FakeApplication.Create();
        var sent = new FakeMail("Support 00018952", "Our reply", new DateTime(2026, 9, 25, 16, 57, 0))
        {
            SenderEmailAddress = "itoke@toyo.co.jp"
        };
        sent.Recipients.Items.Add(new FakeRecipient(1, "customer@cec-ltd.co.jp"));
        app.Sent.Entries.Add(("actual-outbound", sent));
        app.Inbox.Entries.Add(("notice", FakeApplication.SalesforceNotice("00018952", new DateTime(2026, 9, 25, 16, 58, 0))));
        var key = new OutlookCaseKey("Klocwork", "00018952", "unused");
        var result = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app })
            .ReadCases([key], CancellationToken.None)[key];

        Assert.Equal("UNCLASSIFIED", result.WorkflowState);
        Assert.Contains("送信済み（お客様/メーカー未確認）", result.Primary.Text);
        Assert.Equal("actual-outbound", result.Primary.Mail?.EntryId);
        Assert.False(result.Primary.Mail?.IsInbound);
    }

    [Fact]
    public void ExplicitVendorHistoryClassifiesManufacturerRecipient()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseStatusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "メーカー連携内容_00018925.txt"), "To: Manufacturer <support@maker.example>\n");
            var app = FakeApplication.Create();
            var sent = new FakeMail("Support 00018925", "Our question", new DateTime(2026, 9, 25, 15, 0, 0))
            {
                SenderEmailAddress = "itoke@toyo.co.jp"
            };
            sent.Recipients.Items.Add(new FakeRecipient(1, "support@maker.example"));
            app.Sent.Entries.Add(("manufacturer-outbound", sent));
            var key = Key with { FolderPath = folder };
            var result = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app })
                .ReadCases([key], CancellationToken.None)[key];
            Assert.Equal("WAITING_MANUFACTURER_REPLY", result.WorkflowState);
            Assert.Contains("メーカーへ確認済み", result.Primary.Text);
            Assert.Null(result.LatestCustomerOutbound);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("00018949", "Checkmarx", "hitachi-systems.com", "2026-09-24", "CxSASTの最新のサポート言語について")]
    [InlineData("00018742", "Checkmarx", "mhi.com", "2026-09-02", "[確認依頼]脆弱性解析結果の確認依頼")]
    [InlineData("00018952", "Klocwork", "cec-ltd.co.jp", "2026-09-24", "Klocwork2026.3でのAL2023のサポートについて")]
    public void OriginalCustomerInquiryLinksActualSentMailWithoutSupportIdInOriginal(
        string supportId, string product, string domain, string noteDate, string subject)
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseStatusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var date = DateTime.Parse(noteDate, System.Globalization.CultureInfo.InvariantCulture);
            const string anchor = "The customer needs confirmation of the current supported configuration before replying.";
            File.WriteAllText(Path.Combine(folder, $"お客様ご相談内容_{supportId}.txt"),
                $"*****追記部_{date.AddHours(14):yyyy/MM/dd HH:mm:ss}(顧客から受信)*****\n件名: {subject}\n{anchor}\n");
            var key = new OutlookCaseKey(product, supportId, folder);
            var app = FakeApplication.Create();
            var original = new FakeMail(subject, "Dear support,\n" + anchor,
                date.AddHours(11)) { SenderEmailAddress = "customer@" + domain };
            app.Inbox.Entries.Add(("original-" + supportId, original));
            var sent = new FakeMail("Re: " + subject + " " + supportId, "Our reply", date.AddDays(1))
            {
                SenderEmailAddress = "itoke@toyo.co.jp"
            };
            sent.Recipients.Items.Add(new FakeRecipient(1, "customer@" + domain));
            sent.Recipients.Items.Add(new FakeRecipient(2, "colleague@" + domain));
            app.Sent.Entries.Add(("sent-" + supportId, sent));
            app.Inbox.Entries.Add(("notice-" + supportId,
                FakeApplication.SalesforceNotice(supportId, date.AddDays(1).AddMinutes(1))));

            var gateway = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app });
            var result = gateway.ReadCases([key], CancellationToken.None)[key];

            Assert.Equal("CUSTOMER_REPLIED", result.WorkflowState);
            Assert.Contains("お客様へ返信済み", result.Primary.Text);
            Assert.Equal("sent-" + supportId, result.LatestCustomerOutbound?.EntryId);
            Assert.Equal("original-" + supportId, result.LatestCustomerInbound?.EntryId);
            Assert.Equal("EXACT_CUSTOMER_ADDRESS", result.ClassificationReason);
            Assert.Equal(1, result.SkippedSystemNotificationCount);
            Assert.True(gateway.OpenMail(key, result.LatestCustomerInbound!, CancellationToken.None));
            Assert.True(original.WasDisplayed);
            Assert.False(gateway.OpenMail(key with { SupportId = "00000000" }, result.LatestCustomerInbound!, CancellationToken.None));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void InquirySubjectWithoutMatchingBodyDoesNotPromoteUnknownRecipient()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseStatusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "お客様ご相談内容_00018949.txt"),
                "*****追記部_2026/09/24 14:00:00(顧客から受信)*****\n件名: Same Subject\n"
                + "This distinctive inquiry text is required to link the original customer mail.\n");
            var key = new OutlookCaseKey("Checkmarx", "00018949", folder);
            var app = FakeApplication.Create();
            app.Inbox.Entries.Add(("wrong-body", new FakeMail("Same Subject", "Unrelated content",
                new DateTime(2026, 9, 24, 11, 0, 0)) { SenderEmailAddress = "stranger@unknown.org" }));
            var sent = new FakeMail("Support 00018949", "Reply", new DateTime(2026, 9, 25, 10, 0, 0))
            {
                SenderEmailAddress = "itoke@toyo.co.jp"
            };
            sent.Recipients.Items.Add(new FakeRecipient(1, "stranger@unknown.org"));
            app.Sent.Entries.Add(("sent", sent));

            var result = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app })
                .ReadCases([key], CancellationToken.None)[key];
            Assert.Equal("UNCLASSIFIED", result.WorkflowState);
            Assert.Null(result.LatestCustomerInbound);
            Assert.Null(result.LatestCustomerOutbound);
            Assert.Equal("UNKNOWN", result.ClassificationReason);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void SalesforceMediatedMakerMailIsNotToyoNotification()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseStatusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "メーカー連携内容_00018925.txt"),
                "To: Checkmarx <chen.chen@checkmarx.com>\n");
            var app = FakeApplication.Create();
            var maker = new FakeMail("Manufacturer reply 00018925", "Actual support answer",
                new DateTime(2026, 9, 25, 15, 0, 0))
            {
                SenderEmailAddress = "noreply@salesforce.com"
            };
            maker.Recipients.Items.Add(new FakeRecipient(2, "chen.chen@checkmarx.com"));
            app.Inbox.Entries.Add(("maker", maker));
            var key = Key with { FolderPath = folder };
            var result = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app })
                .ReadCases([key], CancellationToken.None)[key];
            Assert.Equal("MANUFACTURER_NEEDS_REPLY", result.WorkflowState);
            Assert.Equal("EXACT_MANUFACTURER_ADDRESS", result.ClassificationReason);
            Assert.Equal("maker", result.LatestManufacturerInbound?.EntryId);
            Assert.Equal(0, result.SkippedSystemNotificationCount);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Case18303SalesforceRelaySentByCurrentUserIsManufacturerOutbound()
    {
        var app = FakeApplication.Create();
        var key = new OutlookCaseKey("Checkmarx", "00018303", "unused");
        var sent = new FakeMail("[IWI] CxOne SAST Analysis Specifications(00018303)",
            "Sent by: Ken Ito at itoke@toyo.co.jp\n\nHello Chen-san,\nCould you confirm the status?",
            new DateTime(2026, 9, 25, 11, 59, 36))
        {
            SenderEmailAddress = "noreply@salesforce.com"
        };
        sent.Recipients.Items.Add(new FakeRecipient(1, "checkmarxsupport@relay.salesforce.com"));
        sent.Recipients.Items.Add(new FakeRecipient(2, "chen.chen@checkmarx.com"));
        sent.Recipients.Items.Add(new FakeRecipient(2, "SS_Support@toyo.co.jp"));
        app.Inbox.Entries.Add(("relayed-outbound", sent));
        var inbound = new FakeMail("Reminder 00018303", "Dear Valued Customer, please reply.",
            new DateTime(2026, 9, 25, 7, 0, 30))
        {
            SenderEmailAddress = "noreply@salesforce.com"
        };
        inbound.Recipients.Items.Add(new FakeRecipient(2, "chen.chen@checkmarx.com"));
        app.Inbox.Entries.Add(("maker-inbound", inbound));
        var gateway = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app });

        var result = gateway.ReadCases([key], CancellationToken.None)[key];

        Assert.Equal("WAITING_MANUFACTURER_REPLY", result.WorkflowState);
        Assert.Equal("relayed-outbound", result.LatestManufacturerOutbound?.EntryId);
        Assert.False(result.Primary.Mail?.IsInbound);
        Assert.Contains("メーカーへ", result.Primary.Text);
        Assert.Equal("CHECKMARX_RELAY_RECIPIENT", result.ClassificationReason);
        Assert.True(gateway.OpenMail(key, result.Primary.Mail!, CancellationToken.None));
        Assert.True(sent.WasDisplayed);
        Assert.False(gateway.OpenMail(key with { SupportId = "00018304" }, result.Primary.Mail!, CancellationToken.None));
    }

    [Theory]
    [InlineData("Dear Valued Customer,\nSent by: Ken Ito at itoke@toyo.co.jp", true)]
    [InlineData("Sent by: Other Person at other@toyo.co.jp\nHello Chen-san", true)]
    [InlineData("Sent by: Ken Ito at itoke@toyo.co.jp\nHello Chen-san", false)]
    public void SalesforceRelayRequiresCurrentUserMarkerOnFirstLine(string body, bool expectedInbound)
    {
        var app = FakeApplication.Create();
        var key = new OutlookCaseKey("Checkmarx", "00018303", "unused");
        var mail = new FakeMail("Support 00018303", body, new DateTime(2026, 9, 25, 12, 0, 0))
        {
            SenderEmailAddress = "noreply@salesforce.com"
        };
        mail.Recipients.Items.Add(new FakeRecipient(2, "chen.chen@checkmarx.com"));
        app.Inbox.Entries.Add(("relay", mail));

        var result = new OutlookCaseStatusGateway(new StubOutlookComGateway { Running = app })
            .ReadCases([key], CancellationToken.None)[key];

        Assert.Equal(expectedInbound, result.Primary.Mail?.IsInbound);
        Assert.Equal(expectedInbound ? "UNCLASSIFIED" : "WAITING_MANUFACTURER_REPLY", result.WorkflowState);
    }

    public sealed class StubOutlookComGateway : IOutlookComGateway
    {
        public object? Running { get; init; }
        public int StartCount { get; private set; }
        public object? TryGetRunningApplication() => Running;
        public object StartApplication() { StartCount++; return new object(); }
        public void ShowCurrentStoreSearch(object application, string query) => throw new NotSupportedException();
    }

    public sealed class FakeApplication
    {
        public FakeFolder Inbox { get; } = new("inbox");
        public FakeFolder Sent { get; } = new("sent");
        public FakeFolder Child { get; } = new("child");
        public FakeMail ChildMail { get; } = new("Support 00018925", "Details", new DateTime(2026, 9, 25, 10, 0, 0));
        public FakeNamespace Session { get; }
        public IEnumerable<FakeFolder> AllFolders => [Inbox, Sent, Child];

        private FakeApplication() => Session = new(this);
        public static FakeApplication Create()
        {
            var app = new FakeApplication();
            app.Inbox.Folders.Items.Add(app.Child);
            app.Child.Entries.Add(("child", app.ChildMail));
            app.Inbox.Entries.Add(("partial", new FakeMail("Support 000189250", "", new DateTime(2026, 9, 25, 9, 0, 0))));
            return app;
        }

        public static FakeApplication CreateRegression()
        {
            var app = Create();
            var inbound = new FakeMail("Support 00018195", "Customer question", new DateTime(2026, 9, 24, 20, 42, 0))
            {
                SenderEmailAddress = "imura-k@mail.dnp.co.jp"
            };
            var outbound = new FakeMail("Support 00018195", "Our reply", new DateTime(2026, 9, 25, 15, 23, 41))
            {
                SenderEmailAddress = "itoke@toyo.co.jp"
            };
            outbound.Recipients.Items.Add(new FakeRecipient(1, "imura-k@mail.dnp.co.jp"));
            outbound.Recipients.Items.Add(new FakeRecipient(2, "exchange-group", "SS_Support@toyo.co.jp"));
            outbound.Recipients.Items.Add(new FakeRecipient(2, "yamashita-a4@mail.dnp.co.jp"));
            app.Inbox.Entries.Add(("customer", inbound));
            app.Inbox.Entries.Add(("sent-copy", outbound));
            app.Inbox.Entries.Add(("notice", SalesforceNotice("00018195", new DateTime(2026, 9, 25, 15, 25, 0))));
            return app;
        }

        public static FakeMail SalesforceNotice(string id, DateTime time) => new(
            $"新規ケース関連メール受信のお知らせ。ケース番号 {id}",
            $"ケース番号 {id} のメールを受信しました。リンクをクリックして確認し、返信してください。https://toyoss.my.salesforce.com/case", time)
        {
            SenderEmailAddress = "noreply@salesforce.com"
        };

        public FakeNamespace GetNamespace(string name) => Session;
    }

    public sealed class FakeNamespace(FakeApplication app)
    {
        public FakeStore DefaultStore { get; } = new();
        public FakeCurrentUser CurrentUser { get; } = new();
        public FakeFolder GetDefaultFolder(int id) => id == 6 ? app.Inbox : app.Sent;
        public FakeMail GetItemFromID(string entryId, string storeId)
        {
            var match = app.AllFolders.SelectMany(folder => folder.Entries)
                .FirstOrDefault(entry => entry.Id == entryId);
            return match.Mail ?? throw new COMException("EntryID not found");
        }
    }

    public sealed class FakeStore
    {
        public bool IsInstantSearchEnabled => true;
    }

    public sealed class FakeFolder(string name)
    {
        public string StoreID => "store";
        public string Name => name;
        public FakeFolders Folders { get; } = new();
        public List<(string Id, FakeMail Mail)> Entries { get; } = new();
        public int GetTableCalls { get; private set; }
        public string LastFilter { get; private set; } = string.Empty;
        public FakeTable GetTable(string filter, int contents)
        {
            GetTableCalls++;
            LastFilter = filter;
            var phrases = Regex.Matches(filter, "ci_phrasematch '([^']+)'", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value).ToArray();
            return new FakeTable(Entries.Where(entry => phrases.Any(phrase =>
                entry.Mail.Subject.Contains(phrase, StringComparison.OrdinalIgnoreCase)
                || entry.Mail.Body.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => new FakeRow(entry.Id)).ToList());
        }
    }

    public sealed class FakeFolders
    {
        public List<FakeFolder> Items { get; } = new();
        public int Count => Items.Count;
        public FakeFolder Item(int index) => Items[index - 1];
    }

    public sealed class FakeTable(List<FakeRow> rows)
    {
        private int position;
        public bool EndOfTable => position >= rows.Count;
        public FakeRow GetNextRow() => rows[position++];
    }

    public sealed class FakeRow(string entryId)
    {
        public string this[string key] => key == "EntryID" ? entryId : string.Empty;
    }

    public sealed class FakeMail(string subject, string body, DateTime time)
    {
        public int Class => 43;
        public string Subject => subject;
        public string Body => body;
        public DateTime ReceivedTime => time;
        public DateTime SentOn => time;
        public string SenderEmailAddress { get; init; } = "unknown@example.com";
        public FakeRecipients Recipients { get; } = new();
        public bool WasDisplayed { get; private set; }
        public void Display(bool modal) => WasDisplayed = true;
    }

    public sealed class FakeCurrentUser
    {
        public FakeAddressEntry AddressEntry { get; } = new();
    }

    public sealed class FakeAddressEntry
    {
        public string Address => "exchange-itoke";
        public FakeExchangeUser GetExchangeUser() => new();
    }

    public sealed class FakeExchangeUser
    {
        public string PrimarySmtpAddress => "itoke@toyo.co.jp";
    }

    public sealed class FakeRecipients
    {
        public List<FakeRecipient> Items { get; } = new();
        public int Count => Items.Count;
        public FakeRecipient Item(int index) => Items[index - 1];
    }

    public sealed class FakeRecipient(int type, string address, string? smtp = null)
    {
        public int Type => type;
        public string Address => address;
        public FakePropertyAccessor PropertyAccessor { get; } = new(smtp ?? address);
    }

    public sealed class FakePropertyAccessor(string address)
    {
        public string GetProperty(string name) => address;
    }
}
