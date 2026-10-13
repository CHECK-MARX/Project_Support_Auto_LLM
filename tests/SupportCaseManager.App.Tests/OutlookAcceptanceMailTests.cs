using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookAcceptanceMailTests
{
    private const string SelfTestBody = """
        本件は、ID: 00999999 で受け付けました。

        ------------ Original Message ------------

        送信者: Test User test@example.com <mailto:test@example.com>
        送信: 2026/09/26 00:50
        宛先: ss_support@toyo.co.jp <mailto:ss_support@toyo.co.jp>
        件名: Outlook受付検知テスト

        これはWPF受付メール自動検知のテストです。
        """;
    private const string Body = """
        株式会社シーイーシー
        内田 栄生 様

        この度は、テクニカルサポートをご利用いただきまして、
        誠にありがとうございます。本件は、ID:  00018952 で受け付けました。
        追って、担当のエンジニアから回答をお送りいたしますので、
        恐れ入りますが少々お待ちください。

        --------------- Original Message ---------------
        送信者: Eio Uchida [eiouchi@cec-ltd.co.jp]
        送信: 2026/09/24 13:34
        宛先: ss_support@toyo.co.jp
        件名: Klocwork2026.3でのAL2023のサポートについて

        株式会社東陽テクニカ
        Klocwork サポートご担当者様
        Amazon Linux 2023 のサポート範囲を教えてください。
        <https://toyoss.my.salesforce.com/servlet/servlet.ImageServer?oid=123>
        thread::abc::
        """;

    private static OutlookAcceptanceEnvelope Mail(string body = Body, string sender = "itoke@toyo.co.jp",
        string[]? to = null, string[]? cc = null) => new("entry", "store", new DateTime(2026, 9, 24, 18, 46, 0),
            "RE: Klocwork2026.3でのAL2023のサポートについて (ID:00018952)", body,
            sender, to ?? ["eiouchi@cec-ltd.co.jp"], cc ?? []);

    [Fact]
    public void ActualReceiptExtractsOriginalInquiryOnly()
    {
        var candidate = OutlookAcceptanceMail.Parse(Mail());
        Assert.NotNull(candidate);
        Assert.Equal("00018952", candidate.SupportId);
        Assert.Equal("株式会社シーイーシー", candidate.Company);
        Assert.Equal("Eio Uchida", candidate.CustomerName);
        Assert.Equal("eiouchi@cec-ltd.co.jp", candidate.CustomerEmail);
        Assert.Equal("2026/09/24 13:34", candidate.OriginalSentAt);
        Assert.Equal("Klocwork2026.3でのAL2023のサポートについて", candidate.OriginalSubject);
        Assert.Contains("Amazon Linux 2023", candidate.InquiryBody);
        Assert.DoesNotContain("本件は、ID", candidate.InquiryBody);
        Assert.DoesNotContain("toyoss.my.salesforce.com", candidate.InquiryBody);
        Assert.Equal("Sender/From", candidate.EvidenceLocation);
    }

    [Fact]
    public void RealSelfToSelfMailIsCandidateWithoutFullToyoGreeting()
    {
        var envelope = Mail(SelfTestBody, to: ["itoke@toyo.co.jp"], cc: ["itoke@toyo.co.jp"]);
        var candidate = Assert.IsType<OutlookAcceptanceCandidate>(OutlookAcceptanceMail.Parse(envelope));
        Assert.Equal("00999999", candidate.SupportId);
        Assert.Equal("Sender/From", candidate.EvidenceLocation);
        Assert.Equal("Test User", candidate.CustomerName);
        Assert.Equal("test@example.com", candidate.CustomerEmail);
        Assert.Equal("Outlook受付検知テスト", candidate.OriginalSubject);
        Assert.True(candidate.HasOriginalMessage);
    }

    [Fact]
    public void RelaxedTemplateOnlyAppliesToSelfAddressedMail()
    {
        Assert.Null(OutlookAcceptanceMail.Parse(Mail(SelfTestBody, sender: "noreply@salesforce.com",
            to: ["itoke@toyo.co.jp"])));
        Assert.Null(OutlookAcceptanceMail.Parse(Mail(SelfTestBody, sender: "itoke@toyo.co.jp",
            to: ["another@example.com"])));
    }

    [Fact]
    public void InboxAndSentCopiesWithSameInternetMessageIdShareMailKey()
    {
        var inbox = Mail(SelfTestBody, to: ["itoke@toyo.co.jp"])
            with { InternetMessageId = "<TEST@EXAMPLE.COM>" };
        var sent = inbox with { EntryId = "sent-copy", InternetMessageId = "<test@example.com>" };
        Assert.Equal(OutlookAcceptanceMail.Parse(inbox)?.MailKey, OutlookAcceptanceMail.Parse(sent)?.MailKey);
        Assert.NotEqual(OutlookAcceptanceMail.Parse(inbox)?.MailKey,
            OutlookAcceptanceMail.Parse(sent with { InternetMessageId = "<other@example.com>" })?.MailKey);
    }

    [Theory]
    [InlineData("itoke@toyo.co.jp", null, null, "Sender/From")]
    [InlineData("noreply@salesforce.com", "itoke@toyo.co.jp", null, "To")]
    [InlineData("noreply@salesforce.com", null, "itoke@toyo.co.jp", "CC")]
    public void CurrentUserSmtpInOutlookEnvelopeIsRequired(string sender, string? to, string? cc, string evidence)
    {
        var candidate = OutlookAcceptanceMail.Parse(Mail(sender: sender,
            to: to is null ? ["eiouchi@cec-ltd.co.jp"] : [to], cc: cc is null ? [] : [cc]));
        Assert.Equal(evidence, candidate?.EvidenceLocation);
    }

    [Fact]
    public void NoCurrentUserAddressIsIgnoredWithoutRejectingSupportId()
    {
        Assert.Null(OutlookAcceptanceMail.Parse(Mail(sender: "noreply@salesforce.com")));
        Assert.Null(OutlookAcceptanceMail.Parse(Mail(sender: "Ken Ito")));
    }

    [Fact]
    public void SentByMustBeAtTopAndContainExactSmtp()
    {
        Assert.Equal("SentBy", OutlookAcceptanceMail.Parse(Mail("Sent by: Ken Ito at itoke@toyo.co.jp\n" + Body,
            sender: "noreply@salesforce.com"))?.EvidenceLocation);
        Assert.Null(OutlookAcceptanceMail.Parse(Mail(Body + "\nSent by: Ken Ito at itoke@toyo.co.jp",
            sender: "noreply@salesforce.com")));
    }

    [Fact]
    public void DelegatedSenderRequiresSmtpNotDisplayName()
    {
        Assert.Equal("SentBy", OutlookAcceptanceMail.Parse(Mail(sender: "noreply@salesforce.com")
            with { DelegatedSenderSmtp = "itoke@toyo.co.jp" })?.EvidenceLocation);
        Assert.Null(OutlookAcceptanceMail.Parse(Mail(sender: "noreply@salesforce.com")
            with { DelegatedSenderSmtp = "Ken Ito" }));
    }

    [Theory]
    [InlineData("ID：　00018952")]
    [InlineData("ID:\n00018952")]
    public void ReceiptIdSpacingVariantsAreSupported(string token)
    {
        Assert.Equal("00018952", OutlookAcceptanceMail.Parse(Mail(Body.Replace("ID:  00018952", token)))?.SupportId);
    }

    [Fact]
    public void QuotedReceiptInLaterReplyIsNotNewAcceptance()
    {
        var reply = "お客様へ回答します。\nFrom: Ken Ito <itoke@toyo.co.jp>\n" + Body;
        Assert.Null(OutlookAcceptanceMail.Parse(Mail(reply)));
    }

    [Fact]
    public void NotificationAndManufacturerMailAreNotAcceptance()
    {
        Assert.Null(OutlookAcceptanceMail.Parse(Mail("ケース番号 00018952 のメールを受信しました。\n"
            + "リンクをクリックして確認し、返信してください。\nhttps://toyoss.my.salesforce.com/case")));
        Assert.Null(OutlookAcceptanceMail.Parse(Mail("Sent by: Ken Ito at itoke@toyo.co.jp\nHello Chen-san,\nManufacturer question",
            sender: "noreply@salesforce.com")));
    }

    [Fact]
    public void MissingOriginalBodyDoesNotPretendToBeComplete()
    {
        var incomplete = Body[..Body.IndexOf("株式会社東陽テクニカ", StringComparison.Ordinal)];
        var candidate = OutlookAcceptanceMail.Parse(Mail(incomplete));
        Assert.NotNull(candidate);
        Assert.False(candidate.HasOriginalMessage);
        Assert.Equal(string.Empty, candidate.InquiryBody);
    }

    [Fact]
    public void MissingOriginalMarkerStillShowsIncompleteCandidate()
    {
        var incomplete = Body[..Body.IndexOf("--------------- Original Message", StringComparison.Ordinal)];
        var candidate = OutlookAcceptanceMail.Parse(Mail(incomplete));
        Assert.NotNull(candidate);
        Assert.False(candidate.HasOriginalMessage);
    }

    [Fact]
    public void InquiryNoteContainsOnlyConfirmedOriginalMessage()
    {
        var candidate = Assert.IsType<OutlookAcceptanceCandidate>(OutlookAcceptanceMail.Parse(Mail()));
        var note = OutlookAcceptanceCandidate.BuildInquiryNote(candidate.OriginalSubject, candidate.CustomerName,
            candidate.CustomerEmail, candidate.OriginalSentAt, candidate.InquiryBody);
        Assert.Contains("件名: Klocwork2026.3でのAL2023のサポートについて", note);
        Assert.Contains("送信者: Eio Uchida <eiouchi@cec-ltd.co.jp>", note);
        Assert.Contains("受信日時: 2026/09/24 13:34", note);
        Assert.Contains("Amazon Linux 2023", note);
        Assert.DoesNotContain("テクニカルサポートをご利用", note);
        Assert.DoesNotContain("toyoss.my.salesforce.com", note);
    }
}
