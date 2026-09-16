using SupportCaseManager.App.ChatGpt;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Config;

namespace SupportCaseManager.App.Tests;

public sealed class GptCaseRegistrationServiceTests
{
    private static readonly ProductGptTarget Target = new(
        "checkmarx",
        "Vulnerability Scanner Assistant",
        "https://chatgpt.com/g/g-test-vulnerability-scanner-assistant");

    [Theory]
    [InlineData("https://chatgpt.com/c/abc", true)]
    [InlineData("https://chatgpt.com/g/g-test/c/abc", true)]
    [InlineData("http://chatgpt.com/c/abc", false)]
    [InlineData("https://example.com/c/abc", false)]
    [InlineData("https://chatgpt.com/", false)]
    public void ConversationUrlValidation_AllowsOnlyChatGptConversations(string value, bool expected)
    {
        Assert.Equal(expected, GptConversationUrl.TryValidateConversation(value, out var normalized));
        if (expected)
        {
            Assert.Equal("https://chatgpt.com/c/abc", normalized);
        }
    }

    [Fact]
    public void ProductResolver_UsesConfiguredTarget()
    {
        var product = new ProductDefinition
        {
            DisplayName = "Checkmarx",
            GptTargetKey = Target.Key,
            GptTargetDisplayName = Target.DisplayName,
            GptLaunchUrl = Target.LaunchUrl,
        };

        var found = new ProductGptTargetResolver().TryResolve(product, out var actual, out _);

        Assert.True(found);
        Assert.Equal(Target, actual);
    }

    [Fact]
    public void ProductResolver_BlocksMissingTarget()
    {
        var found = new ProductGptTargetResolver().TryResolve(
            new ProductDefinition { DisplayName = "Other" },
            out _,
            out var error);

        Assert.False(found);
        Assert.Contains("設定されていません", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Checkmarx", "Vulnerability Scanner Assistant")]
    [InlineData("Klocwork", "Klocwork")]
    [InlineData("QAC", "Helix QAC")]
    public void ProductResolver_UsesConfiguredDefaultsForSupportedProducts(
        string productName,
        string expectedDisplayName)
    {
        var defaults = ProductDefinitionDefaults.GetInitialGptTarget(productName);
        var product = new ProductDefinition
        {
            DisplayName = productName,
            GptTargetKey = defaults.Key,
            GptTargetDisplayName = defaults.DisplayName,
            GptLaunchUrl = defaults.LaunchUrl,
        };

        var found = new ProductGptTargetResolver().TryResolve(product, out var actual, out _);

        Assert.True(found);
        Assert.Equal(expectedDisplayName, actual.DisplayName);
        Assert.StartsWith("https://chatgpt.com/g/", actual.LaunchUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterNew_SavesValidatedConversationUrl()
    {
        var conversation = new FakeConversationService(new(
            GptConversationCreationStatus.Succeeded,
            "https://chatgpt.com/g/g-test/c/conversation-1",
            string.Empty));
        var service = new GptCaseRegistrationService(
            conversation,
            () => new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(9)));

        var result = await service.RegisterNewAsync(Case(), "Checkmarx", Target, "approved brief");

        Assert.Equal(GptRegistrationUpdateStatus.Registered, result.Status);
        Assert.NotNull(result.Registration);
        Assert.Equal("https://chatgpt.com/c/conversation-1", result.Registration.ConversationUrl);
        Assert.Equal(GptRegistrationLinkModes.CreatedByApp, result.Registration.LinkMode);
        Assert.Equal(GptRegistrationStates.Registered, result.Registration.RegistrationState);
        Assert.Equal(1, conversation.CreateCalls);
    }

    [Fact]
    public async Task RegisterNew_BlocksDuplicateWithoutBrowserCall()
    {
        var caseRecord = Case();
        caseRecord.GptRegistration = Registration();
        var conversation = new FakeConversationService(new(
            GptConversationCreationStatus.Succeeded,
            "https://chatgpt.com/c/new",
            string.Empty));

        var result = await new GptCaseRegistrationService(conversation)
            .RegisterNewAsync(caseRecord, "Checkmarx", Target, "brief");

        Assert.Equal(GptRegistrationUpdateStatus.Blocked, result.Status);
        Assert.Equal(0, conversation.CreateCalls);
    }

    [Fact]
    public async Task RegisterNew_PostSendUrlFailureRequiresRelinkWithoutRetry()
    {
        var conversation = new FakeConversationService(new(
            GptConversationCreationStatus.SentButUrlUnavailable,
            string.Empty,
            "missing URL"));

        var result = await new GptCaseRegistrationService(conversation)
            .RegisterNewAsync(Case(), "Checkmarx", Target, "brief");

        Assert.Equal(GptRegistrationUpdateStatus.NeedsRelink, result.Status);
        Assert.Equal(GptRegistrationStates.NeedsRelink, result.Registration?.RegistrationState);
        Assert.Equal(1, conversation.CreateCalls);
    }

    [Fact]
    public async Task RegisterNew_InvalidPostSendUrlRequiresRelinkWithoutRetry()
    {
        var conversation = new FakeConversationService(new(
            GptConversationCreationStatus.Succeeded,
            "https://example.com/not-a-conversation",
            string.Empty));

        var result = await new GptCaseRegistrationService(conversation)
            .RegisterNewAsync(Case(), "Checkmarx", Target, "brief");

        Assert.Equal(GptRegistrationUpdateStatus.NeedsRelink, result.Status);
        Assert.Equal(GptRegistrationStates.NeedsRelink, result.Registration?.RegistrationState);
        Assert.Equal(1, conversation.CreateCalls);
    }

    [Fact]
    public void LinkExisting_SavesLinkModeAndNormalizedUrl()
    {
        var result = new GptCaseRegistrationService(new FakeConversationService())
            .LinkExisting(Case(), "Checkmarx", Target, "https://chatgpt.com/c/existing");

        Assert.Equal(GptRegistrationUpdateStatus.Registered, result.Status);
        Assert.Equal(GptRegistrationLinkModes.ExistingChatLinked, result.Registration?.LinkMode);
        Assert.Equal("https://chatgpt.com/c/existing", result.Registration?.ConversationUrl);
    }

    [Fact]
    public void LinkExisting_RejectsInvalidUrlWithoutRegistration()
    {
        var result = new GptCaseRegistrationService(new FakeConversationService())
            .LinkExisting(Case(), "Checkmarx", Target, "https://example.com/c/existing");

        Assert.Equal(GptRegistrationUpdateStatus.Failed, result.Status);
        Assert.Null(result.Registration);
    }

    [Fact]
    public void LinkExisting_BlocksProductMismatch()
    {
        var caseRecord = Case();
        caseRecord.GptRegistration = Registration();
        caseRecord.GptRegistration.Product = "Klocwork";
        caseRecord.GptRegistration.TargetGptKey = "klocwork";

        var result = new GptCaseRegistrationService(new FakeConversationService())
            .LinkExisting(caseRecord, "Checkmarx", Target, "https://chatgpt.com/c/existing");

        Assert.Equal(GptRegistrationUpdateStatus.Blocked, result.Status);
    }

    [Fact]
    public void LinkExisting_BlocksSupportIdMismatch()
    {
        var caseRecord = Case();
        caseRecord.GptRegistration = Registration();
        caseRecord.GptRegistration.SupportId = "00019999";

        var result = new GptCaseRegistrationService(new FakeConversationService())
            .LinkExisting(caseRecord, "Checkmarx", Target, "https://chatgpt.com/c/existing");

        Assert.Equal(GptRegistrationUpdateStatus.Blocked, result.Status);
    }

    [Fact]
    public async Task Open_UsesSavedConversationUrlDirectly()
    {
        var conversation = new FakeConversationService();

        await new GptCaseRegistrationService(conversation)
            .OpenAsync("https://chatgpt.com/c/existing");

        Assert.Equal("https://chatgpt.com/c/existing", conversation.OpenedUrl);
    }

    [Fact]
    public async Task SendHandoffPrompt_UsesRegisteredExistingConversationAndFixedContract()
    {
        var caseRecord = Case();
        caseRecord.GptRegistration = Registration();
        var conversation = new FakeConversationService();

        var result = await new GptCaseRegistrationService(conversation)
            .SendHandoffPromptAsync(caseRecord);

        Assert.True(result.Succeeded);
        Assert.Equal("https://chatgpt.com/c/existing", conversation.SentUrl);
        Assert.Contains("<<<AI_HANDOFF_V1>>>", conversation.SentMessage, StringComparison.Ordinal);
        Assert.Contains("【現在の未解決事項】", conversation.SentMessage, StringComparison.Ordinal);
        Assert.Contains("<<<END_AI_HANDOFF_V1>>>", conversation.SentMessage, StringComparison.Ordinal);
        Assert.Equal(0, conversation.CreateCalls);
    }

    [Fact]
    public async Task SendHandoffPrompt_BlocksUnregisteredOrMismatchedCaseWithoutSending()
    {
        var conversation = new FakeConversationService();
        var service = new GptCaseRegistrationService(conversation);

        var unregistered = await service.SendHandoffPromptAsync(Case());
        var mismatchedCase = Case();
        mismatchedCase.GptRegistration = Registration();
        mismatchedCase.GptRegistration.SupportId = "00019999";
        var mismatched = await service.SendHandoffPromptAsync(mismatchedCase);

        Assert.False(unregistered.Succeeded);
        Assert.False(mismatched.Succeeded);
        Assert.Empty(conversation.SentUrl);
    }

    [Fact]
    public async Task ConversationService_SendMessageUsesNormalizedExistingConversationOnly()
    {
        var gateway = new FakeConversationGateway();

        await new GptConversationService(gateway).SendMessageAsync(
            "https://chatgpt.com/g/g-test/c/existing",
            "handoff prompt");

        Assert.Equal("https://chatgpt.com/c/existing", gateway.SentUrl);
        Assert.Equal("handoff prompt", gateway.SentMessage);
        Assert.Equal(0, gateway.CreateCalls);
    }

    private static CaseRecord Case() => new(
        "Company",
        "00018303",
        "調査中",
        "20260916",
        "20260916(Company_00018303)",
        @"C:\Cases\20260916(Company_00018303)",
        "2026-09-16T00:00:00Z");

    private static GptCaseRegistration Registration() => new()
    {
        SupportId = "00018303",
        Product = "Checkmarx",
        TargetGptKey = "checkmarx",
        TargetGptDisplayName = "Vulnerability Scanner Assistant",
        ConversationUrl = "https://chatgpt.com/c/existing",
        RegisteredAt = "2026-09-16T12:00:00+09:00",
        LinkMode = GptRegistrationLinkModes.CreatedByApp,
        RegistrationState = GptRegistrationStates.Registered,
    };

    private sealed class FakeConversationService : IGptConversationService
    {
        private readonly GptConversationCreationResult result;

        public FakeConversationService(GptConversationCreationResult? result = null)
        {
            this.result = result ?? new(
                GptConversationCreationStatus.Failed,
                string.Empty,
                "not configured");
        }

        public int CreateCalls { get; private set; }
        public string OpenedUrl { get; private set; } = string.Empty;
        public string SentUrl { get; private set; } = string.Empty;
        public string SentMessage { get; private set; } = string.Empty;

        public Task<GptConversationCreationResult> CreateConversationAsync(
            ProductGptTarget target,
            string approvedBrief,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(result);
        }

        public Task OpenConversationAsync(string conversationUrl, CancellationToken cancellationToken = default)
        {
            OpenedUrl = conversationUrl;
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(
            string conversationUrl,
            string message,
            CancellationToken cancellationToken = default)
        {
            SentUrl = conversationUrl;
            SentMessage = message;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConversationGateway : IGptConversationGateway
    {
        public int CreateCalls { get; private set; }
        public string SentUrl { get; private set; } = string.Empty;
        public string SentMessage { get; private set; } = string.Empty;

        public Task<GptConversationCreationResult> CreateConversationAsync(
            ProductGptTarget target,
            string approvedBrief,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult(new GptConversationCreationResult(
                GptConversationCreationStatus.Failed,
                string.Empty,
                string.Empty));
        }

        public Task OpenConversationAsync(string conversationUrl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendMessageAsync(string conversationUrl, string message, CancellationToken cancellationToken)
        {
            SentUrl = conversationUrl;
            SentMessage = message;
            return Task.CompletedTask;
        }
    }
}
