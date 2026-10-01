using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookAcceptanceGatewayTests
{
    private const string Receipt = """
        株式会社シーイーシー
        内田様
        この度は、テクニカルサポートをご利用いただきまして、
        本件は、ID: 00018952 で受け付けました。
        追って、担当のエンジニアから回答をお送りいたします。
        --------------- Original Message ---------------
        送信者: Eio Uchida [eiouchi@cec-ltd.co.jp]
        送信: 2026/09/24 13:34
        件名: Klocwork support

        Original customer question.
        """;

    [Fact]
    public void ExchangeCcIsResolvedAndRevalidationRejectsChangedMail()
    {
        var app = new FakeApplication();
        var mail = new FakeMail("mail-1", Receipt)
        {
            SenderEmailAddress = "noreply@salesforce.com"
        };
        mail.Recipients.Items.Add(new OutlookCaseStatusGatewayTests.FakeRecipient(2,
            "exchange-itoke", "itoke@toyo.co.jp"));
        app.Inbox.Items.Entries.Add(mail);
        var gateway = new OutlookAcceptanceGateway(new OutlookCaseStatusGatewayTests.StubOutlookComGateway { Running = app });

        var found = gateway.Scan(new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        var candidate = Assert.Single(found);
        Assert.Equal("CC", candidate.EvidenceLocation);
        Assert.True(gateway.Revalidate(candidate));
        mail.Body += "Changed";
        Assert.False(gateway.Revalidate(candidate));
    }

    [Fact]
    public void NoCurrentUserAddressNeverBecomesCandidate()
    {
        var app = new FakeApplication();
        app.Inbox.Items.Entries.Add(new FakeMail("mail-1", Receipt)
        {
            SenderEmailAddress = "noreply@salesforce.com"
        });
        var gateway = new OutlookAcceptanceGateway(new OutlookCaseStatusGatewayTests.StubOutlookComGateway { Running = app });
        Assert.Empty(gateway.Scan(new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None));
    }

    [Fact]
    public void MailBeforeActivationIsNotReturnedEvenIfOutlookFilterOverIncludes()
    {
        var app = new FakeApplication();
        app.Inbox.Items.Entries.Add(new FakeMail("old", Receipt));
        var gateway = new OutlookAcceptanceGateway(new OutlookCaseStatusGatewayTests.StubOutlookComGateway { Running = app });
        Assert.Empty(gateway.Scan(new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None));
    }

    public sealed class FakeApplication
    {
        public FakeFolder Inbox { get; } = new();
        public FakeFolder Sent { get; } = new();
        public FakeNamespace GetNamespace(string name) => new(this);
    }

    public sealed class FakeNamespace(FakeApplication app)
    {
        public FakeFolder GetDefaultFolder(int id) => id == 6 ? app.Inbox : app.Sent;
        public FakeMail GetItemFromID(string entryId, string storeId) =>
            app.Inbox.Items.Entries.Concat(app.Sent.Items.Entries).Single(item => item.EntryID == entryId);
    }

    public sealed class FakeFolder
    {
        public string StoreID => "store";
        public FakeItems Items { get; } = new();
        public FakeFolders Folders { get; } = new();
    }

    public sealed class FakeFolders
    {
        public int Count => 0;
    }

    public sealed class FakeItems
    {
        public List<FakeMail> Entries { get; } = new();
        public FakeItems Restrict(string filter) => this;
        public void Sort(string field, bool descending) { }
        public int Count => Entries.Count;
        public FakeMail Item(int index) => Entries[index - 1];
    }

    public sealed class FakeMail(string entryId, string body)
    {
        public int Class => 43;
        public string EntryID => entryId;
        public string Subject => "Receipt 00018952";
        public string Body { get; set; } = body;
        public DateTime ReceivedTime => new(2026, 9, 24, 18, 46, 0);
        public DateTime SentOn => ReceivedTime;
        public string SenderEmailAddress { get; init; } = "itoke@toyo.co.jp";
        public OutlookCaseStatusGatewayTests.FakeRecipients Recipients { get; } = new();
    }
}
