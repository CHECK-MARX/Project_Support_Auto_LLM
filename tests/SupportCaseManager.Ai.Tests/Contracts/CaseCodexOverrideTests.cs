using System.Text.Json;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Tests.Contracts;

public sealed class CaseCodexOverrideTests
{
    [Fact]
    public void CaseCodexOverrides_RoundTripUsingExistingSettingsJson()
    {
        var productId = Guid.NewGuid();
        var settings = new AiAssistantSettings
        {
            CodexModel = "gpt-6-astra",
            CodexReasoningEffort = "xhigh",
            CaseCodexOverrides =
            [
                new CaseCodexOverride
                {
                    SupportId = "SYN-0001",
                    ProductId = productId,
                    CaseFolder = @"C:\cases\SYN-0001",
                    CaseCodexModel = "gpt-5.6-luna",
                    CaseCodexReasoningEffort = "medium",
                },
            ],
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AiAssistantSettings>(json);
        var selection = Assert.Single(restored!.CaseCodexOverrides);

        Assert.Contains("\"caseCodexOverrides\"", json, StringComparison.Ordinal);
        Assert.Equal("gpt-6-astra", restored.CodexModel);
        Assert.Equal("xhigh", restored.CodexReasoningEffort);
        Assert.Equal("SYN-0001", selection.SupportId);
        Assert.Equal(productId, selection.ProductId);
        Assert.Equal("gpt-5.6-luna", selection.CaseCodexModel);
        Assert.Equal("medium", selection.CaseCodexReasoningEffort);
    }
}
