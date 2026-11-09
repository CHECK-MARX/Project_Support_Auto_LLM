using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookMailMetadataTests
{
    [Theory]
    [InlineData("Auto-Submitted: auto-replied\r\n", "AUTO_SUBMITTED_AUTO_REPLIED")]
    [InlineData("Auto-Submitted:\r\n\tauto-replied; owner-email=agent@example.com\r\n", "AUTO_SUBMITTED_AUTO_REPLIED")]
    [InlineData("X-Autoreply: yes\r\n", "AUTO_REPLY_HEADER")]
    [InlineData("Auto-Submitted: auto-generated\r\nX-Auto-Response-Suppress: DR, RN, NRN, OOF, AutoReply\r\n", "")]
    [InlineData("Auto-Submitted: no\r\n", "")]
    public void ExplicitAutoReplyMetadataDiffersFromPortalTransportMetadata(string headers, string expected) =>
        Assert.Equal(expected, OutlookMailMetadata.AutomaticResponseReason("Re: support question", "Hi, please provide the scan log.", headers, "IPM.Note"));

    [Theory]
    [InlineData("Automatic reply: Support question", "I am away", true)]
    [InlineData("自動返信: お問い合わせ", "担当者が確認します", true)]
    [InlineData("[Request received - 12345] Scan question", "Thank you for contacting support.", true)]
    [InlineData("Reminder: We have not received any response regarding Ticket 12345", "We have not received any response from you.", true)]
    [InlineData("RE: Reminder: We have not received any response regarding Ticket 12345", "Hi, we recommend opening a new ticket.", false)]
    [InlineData("Re: [Request received - 12345] Scan question", "The fix is available in the next release.", false)]
    [InlineData("Support question", "Thanks for your email.\n> This is an automated response", false)]
    [InlineData("Support question", "Thanks for your email.\nFrom: Original Sender\nThis is an automated response", false)]
    [InlineData("Support question", "This is an automated response. Your request has been received.", true)]
    public void ReceiptsRemindersAndQuotedTemplatesDoNotConfuseHumanReplies(string subject, string body, bool automatic) =>
        Assert.Equal(automatic, OutlookMailMetadata.AutomaticResponseReason(subject, body, "", "IPM.Note").Length > 0);

    [Fact]
    public void OriginalAuthorUsesFromRatherThanPortalEnvelopeOrReplyTo()
    {
        const string headers = "From: Support Team\r\n <agent@maker.example>\r\nReply-To: Ken <itoke@toyo.co.jp>\r\n";
        Assert.Equal("agent@maker.example", OutlookMailMetadata.OriginalSender(headers));
        Assert.Empty(OutlookMailMetadata.OriginalSender("Reply-To: agent@maker.example\r\n"));
    }
}
