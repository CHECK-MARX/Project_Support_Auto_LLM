using System.Threading;
using System.Threading.Tasks;
using SupportCaseManager.App.ChatGpt;

namespace SupportCaseManager.App.Tests;

public sealed class ChatGptHistorySearchServiceTests
{
    [Fact]
    public async Task EmptySupportId_DoesNothing()
    {
        var gateway = new FakeGateway();

        var result = await new ChatGptHistorySearchService(gateway)
            .SearchExistingConversationAsync("  ");

        Assert.Equal(ChatGptHistorySearchStatus.Skipped, result.Status);
        Assert.Null(gateway.SupportId);
    }

    [Fact]
    public async Task SupportId_IsTrimmedAndPassedToBrowserGateway()
    {
        var gateway = new FakeGateway();

        var result = await new ChatGptHistorySearchService(gateway)
            .SearchExistingConversationAsync(" 00018561 ");

        Assert.Equal(ChatGptHistorySearchStatus.Succeeded, result.Status);
        Assert.Equal("00018561", gateway.SupportId);
    }

    [Fact]
    public async Task BrowserFailure_IsReturnedWithoutThrowing()
    {
        var gateway = new FakeGateway { Failure = true };

        var result = await new ChatGptHistorySearchService(gateway)
            .SearchExistingConversationAsync("00018561");

        Assert.Equal(ChatGptHistorySearchStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Cancellation_IsReturnedAsFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new ChatGptHistorySearchService(new FakeGateway())
            .SearchExistingConversationAsync("00018561", cancellation.Token);

        Assert.Equal(ChatGptHistorySearchStatus.Failed, result.Status);
    }

    [Fact]
    public void BrowserGatewayUsesChatGptWebUrl()
    {
        Assert.Equal("https://chatgpt.com/", ChatGptBrowserGateway.ChatGptUrl);
    }

    [Fact]
    public void ComposerIdentity_AcceptsCurrentNamelessIdButRejectsBrowserAddressBar()
    {
        Assert.True(ChatGptBrowserGateway.IsPromptInputIdentity(
            string.Empty, "ChatGPT に聞く", string.Empty));
        Assert.True(ChatGptBrowserGateway.IsPromptInputIdentity(
            ChatGptBrowserGateway.PromptInputAutomationId, string.Empty, string.Empty));
        Assert.False(ChatGptBrowserGateway.IsPromptInputIdentity(
            string.Empty, "アドレスと検索バー", string.Empty));
        Assert.False(ChatGptBrowserGateway.IsPromptInputIdentity(
            string.Empty, "Search chats", string.Empty));
    }

    [Fact]
    public void ComposerReadBack_ValuePatternMatchesApprovedBrief()
    {
        const string brief = "Support ID：00018949\n案件引継ぎ内容";

        Assert.True(ChatGptBrowserGateway.ComposerReadBackMatches(brief, brief, null));
    }

    [Fact]
    public void ComposerReadBack_TextPatternCanConfirmWhenValuePatternIsUnavailable()
    {
        const string brief = "Support ID：00018949\n案件引継ぎ内容";

        Assert.True(ChatGptBrowserGateway.ComposerReadBackMatches(brief, null, brief));
    }

    [Fact]
    public void ComposerReadBack_NormalizesLineEndingsAndWhitespace()
    {
        const string brief = "Support ID：00018949\r\n案件引継ぎ内容\r\n";
        const string uiText = "Support ID：00018949\n案件引継ぎ内容  ";

        Assert.True(ChatGptBrowserGateway.ComposerReadBackMatches(brief, string.Empty, uiText));
    }

    [Fact]
    public void ComposerReadBack_RejectsMissingSupportIdAndPartialContent()
    {
        const string brief = "Support ID：00018949\n案件引継ぎ内容";

        Assert.False(ChatGptBrowserGateway.ComposerReadBackMatches(
            brief, "案件引継ぎ内容", "Support ID：00018949"));
    }

    [Fact]
    public void SubmissionConfirmation_RequiresReadableAndEmptyComposer()
    {
        Assert.True(ChatGptBrowserGateway.ComposerIsCleared(true, string.Empty, true, " "));
        Assert.True(ChatGptBrowserGateway.ComposerIsCleared(false, null, true, string.Empty));
        Assert.True(ChatGptBrowserGateway.ComposerIsCleared(false, null, true, "ChatGPT に聞く"));
        Assert.False(ChatGptBrowserGateway.ComposerIsCleared(false, null, false, null));
        Assert.False(ChatGptBrowserGateway.ComposerIsCleared(true, "<<<AI_HANDOFF_V1>>>", false, null));
        Assert.False(ChatGptBrowserGateway.ComposerIsCleared(false, null, true, "<<<AI_HANDOFF_V1>>>"));
    }

    [Fact]
    public void TargetIdentity_RequiresExactCustomGptUrlAndName()
    {
        const string target = "https://chatgpt.com/g/g-checkmarx";

        Assert.True(ChatGptBrowserGateway.TargetIdentityMatches(
            target, target, "Vulnerability Scanner Assistant", "Vulnerability Scanner Assistant - ChatGPT", null));
        Assert.False(ChatGptBrowserGateway.TargetIdentityMatches(
            target, "https://chatgpt.com/g/g-klocwork", "Vulnerability Scanner Assistant",
            "Vulnerability Scanner Assistant - ChatGPT", null));
        Assert.False(ChatGptBrowserGateway.TargetIdentityMatches(
            target, target, "Vulnerability Scanner Assistant", "Klocwork - ChatGPT", null));
        Assert.False(ChatGptBrowserGateway.TargetIdentityMatches(
            target, "https://www.bing.com/search?q=00018949", "Vulnerability Scanner Assistant",
            "Vulnerability Scanner Assistant - ChatGPT", null));
    }

    [Fact]
    public void ActivePageIdentity_UsesConversationDocumentRatherThanChatTitle()
    {
        const string expected = "https://chatgpt.com/c/6ab4f22f-e0b4-83e8-9a55-a83324734aa4";
        const string document = "https://chatgpt.com/g/g-klocwork/c/6ab4f22f-e0b4-83e8-9a55-a83324734aa4";
        const string otherChat = "https://chatgpt.com/c/6a7170f9-3a8c-83ee-be31-8930daab025d";

        Assert.True(ChatGptBrowserGateway.SelectedTabMatchesDocument(
            "AL2023サポート確認", "AL2023サポート確認 - メモリ使用量 - 433 MB"));
        Assert.False(ChatGptBrowserGateway.SelectedTabMatchesDocument(
            "AL2023サポート確認", "別のチャット - メモリ使用量 - 433 MB"));
        Assert.True(ChatGptBrowserGateway.DocumentConversationMatches(expected, document));
        Assert.False(ChatGptBrowserGateway.DocumentConversationMatches(expected, otherChat));
        Assert.False(ChatGptBrowserGateway.DocumentConversationMatches(expected, null));
    }

    [Fact]
    public async Task ExistingConversationSend_WithoutRegisteredTargetNameStopsBeforeOpeningBrowser()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChatGptBrowserGateway().SendMessageAsync(
                "https://chatgpt.com/c/existing", "handoff", CancellationToken.None));
    }

    private sealed class FakeGateway : IChatGptBrowserGateway
    {
        public string? SupportId { get; private set; }
        public bool Failure { get; init; }

        public Task OpenHistorySearchAsync(string supportId, CancellationToken cancellationToken)
        {
            if (Failure)
            {
                throw new InvalidOperationException("browser failure");
            }

            SupportId = supportId;
            return Task.CompletedTask;
        }
    }
}
