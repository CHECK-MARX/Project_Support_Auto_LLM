using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class RagLabEvidencePromptFormatterTests
{
    [Fact]
    public void Format_UsesStructuredSectionAndDoesNotInferMissingOptionalFields()
    {
        var result = new RagLabEvidencePromptFormatter().Format(
        [
            new RagLabEvidenceItem
            {
                SourceType = "SyntheticManual",
                DocumentId = "doc-1",
                SupportId = "SYN-0001",
                Product = "Checkmarx",
                Version = "1.0",
                Score = 0.75,
                SelectionReason = "人工理由",
                Warnings = ["人工警告"],
                Text = "人工本文",
            },
        ]);

        Assert.StartsWith("[RAG Evidence]", result, StringComparison.Ordinal);
        Assert.EndsWith("[End RAG Evidence]", result, StringComparison.Ordinal);
        Assert.Contains("Source: SyntheticManual", result);
        Assert.Contains("Document ID: doc-1", result);
        Assert.Contains("Support ID: SYN-0001", result);
        Assert.Contains("Score: 0.75", result);
        Assert.Contains("Content:\n人工本文", result.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Product match:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Version match:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Possibly stale:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Possible conflict:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Unverified fields:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_EmptyEvidence_ReturnsEmptyText()
    {
        Assert.Empty(new RagLabEvidencePromptFormatter().Format([]));
    }
}
