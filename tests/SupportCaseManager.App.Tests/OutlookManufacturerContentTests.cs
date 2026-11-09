using SupportCaseManager.App.Outlook;

namespace SupportCaseManager.App.Tests;

public sealed class OutlookManufacturerContentTests
{
    private const string Escalation = "I am forwarding the ticket to the AppSec team in order to assist you further.";

    [Theory]
    [InlineData("Hi Ken,\r\nThank you for contacting Checkmarx support team.\r\nPlease be informed that I am forwarding the ticket to the AppSec team in order to assist you further. We appreciate your patience and happy to assist you further.\r\nClick here to see your request <https://support.example.com/case/123>\r\nThanks and Regards,\r\nSupport engineer")]
    [InlineData("Hi Ken,\nI am forwarding the ticket to the AppSec team in order to investigate the details and assist you further.\nWe appreciate your patience and happy to assist you further.")]
    [InlineData("We have escalated this case to our engineering team for further investigation.")]
    [InlineData("Your case has been escalated to our engineering team.")]
    [InlineData("担当部門へ引き継ぎました。")]
    [InlineData("現在、調査中です。進展があり次第ご連絡します。")]
    public void OnlyUnderstoodProgressMessagesAreInformational(string body)
    {
        var result = OutlookManufacturerContent.Classify(body);
        Assert.Equal(OutlookManufacturerMessageKind.ProgressOnly, result.Kind);
        Assert.Contains("回答待ち", result.Summary);
    }

    [Theory]
    [InlineData("Please provide the scan logs.")]
    [InlineData("Could you confirm the scanner version?")]
    [InlineData("Please try restarting the service.")]
    [InlineData("We need the project archive.")]
    [InlineData("ログをご提供ください。")]
    public void MixedEscalationAndRequestRequiresAttention(string request)
    {
        Assert.Equal(OutlookManufacturerMessageKind.ActionRequested,
            OutlookManufacturerContent.Classify(Escalation + "\n" + request).Kind);
    }

    [Theory]
    [InlineData("The issue is fixed in version 2026.3.")]
    [InlineData("The setting is not supported on Linux.")]
    [InlineData("A workaround is available: use the legacy mode.")]
    [InlineData("We cannot escalate the ticket to the engineering team.")]
    [InlineData("We will escalate the ticket if the issue is reproducible.")]
    [InlineData("There will be a maintenance window tomorrow at 10:00.")]
    public void UnknownTechnicalOrConditionalContentCannotLoseReviewAlert(string additional)
    {
        Assert.Equal(OutlookManufacturerMessageKind.Unclassified,
            OutlookManufacturerContent.Classify(Escalation + "\n" + additional).Kind);
    }

    [Theory]
    [InlineData("From: Previous Agent")]
    [InlineData("> Previous message")]
    [InlineData("-----Original Message-----")]
    [InlineData("On Wednesday, Agent wrote:")]
    public void QuotedEscalationDoesNotDowngradeNewAnswer(string boundary)
    {
        Assert.Equal(OutlookManufacturerMessageKind.Unclassified,
            OutlookManufacturerContent.Classify("The patch is ready.\n" + boundary + "\n" + Escalation).Kind);
        Assert.Equal(OutlookManufacturerMessageKind.ProgressOnly,
            OutlookManufacturerContent.Classify(Escalation + "\n" + boundary + "\nPlease provide the old logs.").Kind);
    }

    [Fact]
    public void SignatureLinksWithQueryStringsAreNotQuestionsButAccompanyingRequestsStillAlert()
    {
        const string footer = "\nBest regards,\nAgent\nDocumentation: https://docs.example.com/?lang=en\n<https://portal.example.com/image?oid=123&from=ext>";
        Assert.Equal(OutlookManufacturerMessageKind.ProgressOnly, OutlookManufacturerContent.Classify(Escalation + footer).Kind);
        Assert.Equal(OutlookManufacturerMessageKind.ActionRequested,
            OutlookManufacturerContent.Classify(Escalation + "\nPlease upload the logs at https://portal.example.com/?case=123" + footer).Kind);
    }

    [Fact]
    public void PostscriptRequestsAreNotLostAtSignatureBoundary()
    {
        Assert.Equal(OutlookManufacturerMessageKind.ActionRequested,
            OutlookManufacturerContent.Classify(Escalation + "\nBest regards,\nAgent\nP.S.: Please provide the logs.").Kind);
        Assert.Equal(OutlookManufacturerMessageKind.Unclassified,
            OutlookManufacturerContent.Classify(Escalation + "\nBest regards,\nAgent\nP.S.: A workaround is available.").Kind);
    }

    [Fact]
    public void TruncatedLongMessageIsNeverMarkedAsProgressOnly() =>
        Assert.Equal(OutlookManufacturerMessageKind.Unclassified,
            OutlookManufacturerContent.Classify(Escalation + "\n" + new string('x', 13000)).Kind);
}
