using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.GptHandoff;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class GptHandoffRegistrationResolverTests
{
    [Fact]
    public void ParentResolvedRegistration_IsAvailableToAiAssistantAndPreservesConversationUrl()
    {
        var context = RegisteredContext();

        var resolved = GptHandoffRegistrationResolver.TryResolve(
            context,
            "00018303",
            "Checkmarx",
            out var registration,
            out var error);

        Assert.True(resolved, error);
        Assert.True(registration.IsRegistered);
        Assert.Equal("00018303", registration.SupportId);
        Assert.Equal("Checkmarx", registration.Product);
        Assert.Equal(context.ConversationUrl, registration.ConversationUrl);
    }

    [Fact]
    public void RegistrationResolution_UsesNormalizedSupportAndProductInsteadOfFolderOrStatus()
    {
        var context = RegisteredContext();

        var beforeRename = GptHandoffRegistrationResolver.IsRegisteredForCurrentCase(
            context,
            "18303",
            "checkmarx");
        var afterRenameAndStatusChange = GptHandoffRegistrationResolver.IsRegisteredForCurrentCase(
            context,
            "00018303",
            "CHECKMARX");

        Assert.True(beforeRename);
        Assert.True(afterRenameAndStatusChange);
    }

    [Theory]
    [InlineData("00019999", "Checkmarx", "Support ID")]
    [InlineData("00018303", "Klocwork", "製品")]
    public void RegistrationResolution_RejectsAnotherCaseOrProduct(
        string supportNumber,
        string productName,
        string expectedReason)
    {
        var resolved = GptHandoffRegistrationResolver.TryResolve(
            RegisteredContext(),
            supportNumber,
            productName,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains(expectedReason, error, StringComparison.Ordinal);
    }

    private static GptHandoffContext RegisteredContext() => new()
    {
        SupportId = "00018303",
        Product = "Checkmarx",
        TargetGptKey = "checkmarx",
        TargetGptDisplayName = "Vulnerability Scanner Assistant",
        ConversationUrl = "https://chatgpt.com/c/6a70b0a5-1018-83ee-b405-11a2a80326f4",
        RegisteredAt = "2026-09-16T22:49:35+09:00",
        LinkMode = GptRegistrationLinkModes.ExistingChatLinked,
        RegistrationState = GptRegistrationStates.Registered,
    };
}
