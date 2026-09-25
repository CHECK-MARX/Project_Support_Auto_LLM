using SupportCaseManager.App.Outlook;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookCaseMailStatusTests
{
    private static readonly OutlookCaseKey Key = new("Checkmarx", "00018925", "case");
    private static readonly OutlookCaseContacts Contacts = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "customer@example.com" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "maker@example.com" });
    private static readonly DateTime Day = new(2026, 9, 25, 0, 0, 0);

    [Theory]
    [InlineData(false, false, false, false, "NO_MAIL", "メールなし")]
    [InlineData(true, false, false, false, "CUSTOMER_NEEDS_REPLY", "お客様から受信")]
    [InlineData(true, true, false, false, "CUSTOMER_REPLIED", "お客様へ返信済み")]
    [InlineData(true, false, false, true, "WAITING_MANUFACTURER_REPLY", "お客様から受信")]
    [InlineData(true, false, true, true, "CUSTOMER_NEEDS_REPLY", "メーカーから回答")]
    [InlineData(true, true, true, true, "CUSTOMER_REPLIED", "お客様へ返信済み")]
    public void CustomerAndManufacturerTimelineStaySeparate(bool customerIn, bool customerOut, bool makerIn, bool makerOut,
        string state, string primary)
    {
        var mail = new List<OutlookMailCandidate>();
        if (customerIn) mail.Add(Candidate(10, true, "customer@example.com"));
        if (makerOut) mail.Add(Candidate(11, false, "maker@example.com"));
        if (makerIn) mail.Add(Candidate(14, true, "maker@example.com"));
        if (customerOut) mail.Add(Candidate(15, false, "customer@example.com"));

        var result = OutlookCaseMailClassifier.Build(Key, Contacts, mail, Day);

        Assert.Equal(state, result.WorkflowState);
        Assert.Contains(primary, result.Primary.Text);
        if (customerIn && makerOut && !customerOut)
        {
            Assert.Null(result.LatestCustomerOutbound);
            Assert.Contains("お客様未返信", result.Secondary.Text);
            if (!makerIn)
            {
                Assert.Equal(result.LatestCustomerInbound, result.Primary.Mail);
                Assert.Equal(result.LatestManufacturerOutbound, result.Secondary.Mail);
            }
        }
    }

    [Fact]
    public void NewCustomerInboundAfterReplyNeedsAnotherReply()
    {
        var result = OutlookCaseMailClassifier.Build(Key, Contacts,
            [Candidate(10, true, "customer@example.com"), Candidate(11, false, "customer@example.com"), Candidate(12, true, "customer@example.com")], Day);
        Assert.Equal("CUSTOMER_NEEDS_REPLY", result.WorkflowState);
        Assert.Contains("お客様から受信", result.Primary.Text);
    }

    [Fact]
    public void ManufacturerReplyThenNewInboundNeedsAnotherReply()
    {
        var result = OutlookCaseMailClassifier.Build(Key, Contacts,
            [Candidate(10, true, "maker@example.com"), Candidate(11, false, "maker@example.com"), Candidate(12, true, "maker@example.com")], Day);
        Assert.Equal("MANUFACTURER_NEEDS_REPLY", result.WorkflowState);
        Assert.Contains("メーカーから回答", result.Primary.Text);
        Assert.NotNull(result.LatestManufacturerOutbound);
    }

    [Fact]
    public void ManufacturerInboundThenOutboundShowsManufacturerReply()
    {
        var result = OutlookCaseMailClassifier.Build(Key, Contacts,
            [Candidate(10, true, "maker@example.com"), Candidate(11, false, "maker@example.com")], Day);
        Assert.Contains("メーカーへ返信済み", result.Primary.Text);
        Assert.Null(result.LatestCustomerOutbound);
    }

    [Fact]
    public void CustomerStillNeedsReplyAfterManufacturerReply()
    {
        var result = OutlookCaseMailClassifier.Build(Key, Contacts,
            [Candidate(10, true, "customer@example.com"), Candidate(11, true, "maker@example.com"),
                Candidate(12, false, "maker@example.com")], Day);
        Assert.Equal("WAITING_MANUFACTURER_REPLY", result.WorkflowState);
        Assert.Contains("メーカーへ返信済み / お客様未返信", result.Secondary.Text);
        Assert.Null(result.LatestCustomerOutbound);
    }

    [Fact]
    public void UnknownSenderAndMixedRecipientsAreNeverClassified()
    {
        var unknown = OutlookCaseMailClassifier.Build(Key, Contacts, [Candidate(10, true, "other@example.com")], Day);
        var mixed = OutlookCaseMailClassifier.Build(Key, Contacts,
            [Candidate(10, false, "customer@example.com", "maker@example.com")], Day);
        Assert.Equal("UNCLASSIFIED", unknown.WorkflowState);
        Assert.Contains("受信（お客様/メーカー未確認）", unknown.Primary.Text);
        Assert.Contains("送信元を案件履歴", unknown.Primary.Explanation);
        Assert.Equal("UNCLASSIFIED", mixed.WorkflowState);
        Assert.Contains("送信済み（お客様/メーカー未確認）", mixed.Primary.Text);
        Assert.Contains("宛先を案件履歴", mixed.Primary.Explanation);
        Assert.Null(mixed.LatestCustomerOutbound);
        Assert.Null(mixed.LatestManufacturerOutbound);
    }

    [Fact]
    public void SalesforceCaseNotificationRequiresSenderSubjectAndBody()
    {
        const string subject = "新規ケース関連メール受信のお知らせ。ケース番号 00018952";
        const string body = "ケース番号 00018952 のメールを受信しました。リンクをクリックして確認し、返信してください。https://toyoss.my.salesforce.com/case";
        Assert.True(OutlookCaseMailClassifier.IsSalesforceCaseNotification("noreply@salesforce.com", subject, body, "00018952"));
        Assert.False(OutlookCaseMailClassifier.IsSalesforceCaseNotification("agent@salesforce.com", subject, body, "00018952"));
        Assert.False(OutlookCaseMailClassifier.IsSalesforceCaseNotification("noreply@salesforce.com", "00018952について", body, "00018952"));
        Assert.False(OutlookCaseMailClassifier.IsSalesforceCaseNotification("noreply@salesforce.com", subject, "通常の案件メール", "00018952"));
        Assert.False(OutlookCaseMailClassifier.IsSalesforceCaseNotification("noreply@salesforce.com", subject, body, "00018952", ["support@maker.example"]));
    }

    [Fact]
    public void LaterSalesforceNotificationDoesNotReplaceActualCaseMail()
    {
        var key = Key with { SupportId = "00018952" };
        var outbound = Candidate(15, false, "customer@example.com") with
        {
            Mail = Candidate(15, false, "customer@example.com").Mail with { SupportId = "00018952" }
        };
        var notification = Candidate(16, true, "noreply@salesforce.com") with
        {
            Mail = Candidate(16, true, "noreply@salesforce.com").Mail with { SupportId = "00018952" },
            IsSystemNotification = true
        };
        var result = OutlookCaseMailClassifier.Build(key, Contacts, [outbound, notification], Day);
        Assert.Equal("CUSTOMER_OUTBOUND", result.WorkflowState);
        Assert.Equal(outbound.Mail, result.Primary.Mail);
        Assert.Null(result.LatestUnclassified);
    }

    [Fact]
    public void CustomerReplyAcceptsKnownCorporateDomainCcButNotManufacturerCc()
    {
        var contacts = new OutlookCaseContacts(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "imura-k@mail.dnp.co.jp" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "support@maker.example" });
        var inbound = Candidate(10, true, "imura-k@mail.dnp.co.jp");
        var outbound = Candidate(15, false, "imura-k@mail.dnp.co.jp") with
        {
            CopyAddresses = ["yamashita-a4@mail.dnp.co.jp"]
        };
        var result = OutlookCaseMailClassifier.Build(Key, contacts, [inbound, outbound], Day);
        Assert.Equal("CUSTOMER_REPLIED", result.WorkflowState);
        Assert.Equal(outbound.Mail, result.LatestCustomerOutbound);

        var mixed = OutlookCaseMailClassifier.Build(Key, contacts,
            [outbound with { CopyAddresses = ["support@maker.example"] }], Day);
        Assert.Equal("UNCLASSIFIED", mixed.WorkflowState);
    }

    [Fact]
    public void CaseConfirmedCorporateDomainsClassifyRelatedAddressesButPublicDomainsDoNot()
    {
        var contacts = new OutlookCaseContacts(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "customer@cec-ltd.co.jp", "private@gmail.com" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent@maker.example" });
        var corporate = OutlookCaseMailClassifier.Build(Key, contacts,
            [Candidate(10, false, "colleague@ml.cec-ltd.co.jp")], Day);
        Assert.Equal("CUSTOMER_OUTBOUND", corporate.WorkflowState);
        Assert.Equal("CASE_CUSTOMER_DOMAIN", corporate.ClassificationReason);

        var publicMail = OutlookCaseMailClassifier.Build(Key, contacts,
            [Candidate(10, false, "unrelated@gmail.com")], Day);
        Assert.Equal("UNCLASSIFIED", publicMail.WorkflowState);
        Assert.Equal("UNKNOWN", publicMail.ClassificationReason);
    }

    [Fact]
    public void ManufacturerCaseDomainClassifiesButProductNameDoesNotInventDomain()
    {
        var contacts = new OutlookCaseContacts(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent@support.checkmarx.com" });
        var maker = OutlookCaseMailClassifier.Build(Key, contacts,
            [Candidate(10, false, "other@support.checkmarx.com")], Day);
        Assert.Equal("WAITING_MANUFACTURER_REPLY", maker.WorkflowState);
        Assert.Equal("CASE_MANUFACTURER_DOMAIN", maker.ClassificationReason);

        var noEvidence = OutlookCaseMailClassifier.Build(Key,
            new OutlookCaseContacts(new HashSet<string>(), new HashSet<string>()),
            [Candidate(10, false, "support@checkmarx.com")], Day);
        Assert.Equal("UNCLASSIFIED", noEvidence.WorkflowState);
    }

    [Fact]
    public void SalesforceMediatedManufacturerMailUsesGroundedCopyRecipient()
    {
        var contacts = new OutlookCaseContacts(new HashSet<string>(),
            new HashSet<string> { "chen.chen@checkmarx.com" });
        var inbound = Candidate(10, true, "noreply@salesforce.com") with
        {
            CopyAddresses = ["chen.chen@checkmarx.com"]
        };
        var result = OutlookCaseMailClassifier.Build(Key, contacts, [inbound], Day);
        Assert.Equal("MANUFACTURER_NEEDS_REPLY", result.WorkflowState);
        Assert.Equal("EXACT_MANUFACTURER_ADDRESS", result.ClassificationReason);
        Assert.Equal(0, result.SkippedSystemNotificationCount);
    }

    [Fact]
    public void SalesforceReminderWithoutKnownManufacturerContactRemainsExplainedUnknown()
    {
        var contacts = new OutlookCaseContacts(
            new HashSet<string> { "cxsast-support@ubsecure.jp" }, new HashSet<string>());
        var inbound = Candidate(12, true, "noreply@salesforce.com") with
        {
            CopyAddresses = ["checkmarxsupport@relay.salesforce.com", "chen.chen@checkmarx.com"]
        };
        var result = OutlookCaseMailClassifier.Build(Key, contacts, [inbound], Day);
        Assert.Equal("UNCLASSIFIED", result.WorkflowState);
        Assert.Contains("受信（お客様/メーカー未確認）", result.Primary.Text);
        Assert.Contains("Salesforceの代理アドレス", result.Primary.Explanation);
    }

    [Fact]
    public void ContactSetsRefreshFromCaseFilesAndDoNotReadGptHandoff()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseContactsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var customerPath = Path.Combine(folder, "お客様ご相談内容_00018925.txt");
            File.WriteAllText(Path.Combine(folder, "GPT連携内容_00018925.txt"), "MAIL: unrelated@other.example\n");
            var key = Key with { FolderPath = folder };
            Assert.Empty(OutlookCaseContactsReader.Read(key).Customer);
            File.WriteAllText(customerPath, "Email: customer@cec-ltd.co.jp\n");
            Assert.Contains("customer@cec-ltd.co.jp", OutlookCaseContactsReader.Read(key).Customer);
            File.AppendAllText(customerPath, "SMTP address: colleague@cec-ltd.co.jp\n");
            Assert.Contains("colleague@cec-ltd.co.jp", OutlookCaseContactsReader.Read(key).Customer);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void StatusDisplayRoundsSecondsLikeOutlookWithoutChangingTimeline()
    {
        var inbound = Candidate(10, true, "customer@example.com");
        var outbound = Candidate(15, false, "customer@example.com") with
        {
            Mail = Candidate(15, false, "customer@example.com").Mail with
            {
                Time = Day.AddHours(15).AddMinutes(23).AddSeconds(41)
            }
        };
        var result = OutlookCaseMailClassifier.Build(Key, Contacts, [inbound, outbound], Day);
        Assert.Equal("09/25 15:24 お客様へ返信済み", result.Primary.Text);
        Assert.Equal(Day.AddHours(15).AddMinutes(23).AddSeconds(41), result.LatestCustomerOutbound?.Time);
    }

    [Theory]
    [InlineData("Support 00018925", true)]
    [InlineData("Support 000189250", false)]
    [InlineData("Support 0001892", false)]
    [InlineData("X00018925", false)]
    [InlineData("00018925X", false)]
    public void SupportIdRequiresExactToken(string text, bool expected) =>
        Assert.Equal(expected, OutlookCaseMailClassifier.ContainsExactSupportId(text, "00018925"));

    [Fact]
    public void AnotherCaseDoesNotEnterThisCaseStatus()
    {
        var other = Candidate(10, true, "customer@example.com") with
        {
            Mail = Candidate(10, true, "customer@example.com").Mail with { SupportId = "00018926" },
        };
        Assert.Equal("NO_MAIL", OutlookCaseMailClassifier.Build(Key, Contacts, [other], Day).WorkflowState);
    }

    [Fact]
    public void CustomerOutboundWithoutKnownInboundIsNotCalledReply()
    {
        var result = OutlookCaseMailClassifier.Build(Key, Contacts, [Candidate(10, false, "customer@example.com")], Day);
        Assert.Equal("CUSTOMER_OUTBOUND", result.WorkflowState);
        Assert.Contains("お客様へ送信済み", result.Primary.Text);
    }

    [Fact]
    public void ContactReaderUsesOnlyExplicitHeadersAndRejectsAmbiguousAddress()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseContactsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "お客様ご相談内容_00018925.txt"),
                "From: Customer <customer@example.com>\nBody: maker@example.com\nFrom: Shared <shared@example.com>");
            File.WriteAllText(Path.Combine(folder, "メーカー連携内容_00018925.txt"),
                "To: Maker <maker@example.com>\nFrom: Shared <shared@example.com>\nCc: other@example.com\n"
                + string.Concat(Enumerable.Repeat("quoted content\n", 12)) + "From: Quoted <quoted@example.com>");
            var contacts = OutlookCaseContactsReader.Read(Key with { FolderPath = folder });
            Assert.Contains("customer@example.com", contacts.Customer);
            Assert.Contains("maker@example.com", contacts.Manufacturer);
            Assert.DoesNotContain("shared@example.com", contacts.Customer);
            Assert.DoesNotContain("shared@example.com", contacts.Manufacturer);
            Assert.DoesNotContain("other@example.com", contacts.Manufacturer);
            Assert.DoesNotContain("quoted@example.com", contacts.Manufacturer);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void CustomerSignatureMailAfterOpeningLinesBecomesCaseContact()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OutlookCaseContactsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "お客様ご相談内容_00018925.txt"),
                "From: Notification <noreply@salesforce.com>\n"
                + string.Concat(Enumerable.Repeat("Inquiry line\n", 15)) + "MAIL：imura-k@mail.dnp.co.jp\n");
            var contacts = OutlookCaseContactsReader.Read(Key with { FolderPath = folder });
            Assert.Contains("imura-k@mail.dnp.co.jp", contacts.Customer);
            Assert.DoesNotContain("noreply@salesforce.com", contacts.Customer);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void BothStatusListsHaveIndependentClickableOutlookLines()
    {
        var xaml = XDocument.Load(FindMainWindowPath());
        var style = xaml.Descendants().Single(element => element.Name.LocalName == "Style"
            && element.Attributes().Any(attribute => attribute.Name.LocalName == "Key"
                && attribute.Value == "OutlookStatusTextStyle"));
        Assert.Contains(style.Descendants(), element => element.Name.LocalName == "DataTrigger"
            && element.Attribute("Value")?.Value == "True"
            && element.Attribute("Binding")?.Value.Contains("Tag.IsInbound", StringComparison.Ordinal) == true
            && element.Descendants().Any(setter => setter.Attribute("Value")?.Value == "{DynamicResource OutlookInboundForeground}"));
        Assert.Contains(style.Descendants(), element => element.Name.LocalName == "DataTrigger"
            && element.Attribute("Value")?.Value == "False"
            && element.Descendants().Any(setter => setter.Attribute("Value")?.Value == "{DynamicResource OutlookOutboundForeground}"));
        foreach (var gridName in new[] { "OpenCaseGrid", "StaleCaseGrid" })
        {
            var grid = xaml.Descendants().Single(element => element.Name.LocalName == "DataGrid"
                && element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == gridName));
            var column = grid.Descendants().Single(element => element.Name.LocalName == "DataGridTemplateColumn"
                && element.Attribute("Header")?.Value == "Outlook状況");
            var lines = column.Descendants().Where(element => element.Name.LocalName == "TextBlock"
                && element.Attribute("MouseLeftButtonDown")?.Value == "OnOutlookStatusLineDoubleClick").ToArray();
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line =>
            {
                Assert.Equal("{StaticResource OutlookStatusTextStyle}", line.Attribute("Style")?.Value);
                Assert.Contains(".Explanation", line.Attribute("ToolTip")?.Value);
            });
        }
    }

    [Fact]
    public async Task CacheIsSharedAndManualRefreshBypassesItWithoutBlockingCaller()
    {
        var gateway = new FakeGateway();
        var service = new OutlookCaseStatusService(gateway);
        var cases = new[] { Key, Key };
        var first = service.RefreshAsync(cases, force: false);
        Assert.False(first.IsCompleted);
        gateway.Release.Set();
        await first;
        await service.RefreshAsync(cases, force: false);
        Assert.Equal(1, gateway.ReadCount);
        Assert.Equal(1, gateway.LastCaseCount);
        await service.RefreshAsync(cases, force: true);
        Assert.Equal(2, gateway.ReadCount);
        Assert.Equal(ApartmentState.STA, gateway.Apartment);
    }

    [Fact]
    public async Task CancelledRefreshDoesNotPublishOldResult()
    {
        var gateway = new FakeGateway();
        var service = new OutlookCaseStatusService(gateway);
        using var source = new CancellationTokenSource();
        var pending = service.RefreshAsync([Key], force: true, source.Token);
        Assert.True(gateway.Started.Wait(TimeSpan.FromSeconds(2)));
        source.Cancel();
        gateway.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await service.RefreshAsync([Key], force: false);
        Assert.Equal(2, gateway.ReadCount);
    }

    private static OutlookMailCandidate Candidate(int hour, bool inbound, params string[] addresses)
    {
        var reference = new OutlookMailReference($"entry-{hour}", "store", Day.AddHours(hour), "Support 00018925",
            inbound ? string.Join(';', addresses) : string.Empty,
            inbound ? string.Empty : string.Join(';', addresses), "00018925", inbound);
        return new(reference, inbound, addresses);
    }

    private sealed class FakeGateway : IOutlookCaseStatusGateway
    {
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim Started { get; } = new();
        public int ReadCount { get; private set; }
        public int LastCaseCount { get; private set; }
        public ApartmentState Apartment { get; private set; }

        public IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus> ReadCases(IReadOnlyList<OutlookCaseKey> cases, CancellationToken token)
        {
            ReadCount++;
            LastCaseCount = cases.Count;
            Apartment = Thread.CurrentThread.GetApartmentState();
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(3));
            return cases.ToDictionary(key => key, _ => OutlookCaseMailStatus.Message("Outlook未起動"));
        }

        public bool OpenMail(OutlookCaseKey key, OutlookMailReference reference, CancellationToken token) => false;
    }

    private static string FindMainWindowPath([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "SupportCaseManager.App", "MainWindow.xaml"));
}
