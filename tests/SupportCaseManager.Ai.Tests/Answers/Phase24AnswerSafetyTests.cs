using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Facts;
using Xunit;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase24AnswerSafetyTests
{
    [Fact]
    public void FragmentedEvidenceDoesNotCreateACommand()
    {
        var request = Request("QACのCLIコマンドを教えてください。", [
            Source("a", "qacli analyze"),
            Source("b", "-P <project-directory>")
        ]);

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException());

        Assert.DoesNotContain("qacli analyze -P <project-directory>", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AnswerReadiness.NeedsReview, result.Readiness);
    }

    [Fact]
    public void MissingOptionIsNotAddedToAnAtomicCommand()
    {
        var request = Request("QACのCLIコマンドを教えてください。", [
            Source("a", "qacli analyze -P <project-directory>")
        ]);

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException());

        Assert.Contains("qacli analyze -P <project-directory>", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--raw-source", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PageAndSectionAreNotInvented()
    {
        var answer = DeterministicAnswerComposer.ComposeHowTo([
            new EvidenceItem
            {
                SourceId = "no-location",
                SourceType = "Manual",
                DocumentTitle = "QAC Manual",
                Excerpt = "準備を確認します。"
            }
        ]);

        Assert.DoesNotContain("Page", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("『", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void PastCaseOnlyProductSpecificationRequiresManufacturerConfirmation()
    {
        var request = Request("QACの対応OSを教えてください。", [Source("past", "Windows") with { SourceType = "PastCase" }]) with
        {
            FactResolution = new FactResolutionResult
            {
                Classification = new QuestionClassificationResult
                {
                    QuestionTypes = [QuestionTypes.FeatureAvailabilityQuestion]
                }
            }
        };

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException());

        Assert.Equal(AnswerReadiness.NeedsManufacturerConfirmation, result.Readiness);
    }

    [Fact]
    public void ConflictingResolutionIsNotCustomerReady()
    {
        var request = Request("QACの対応OSを教えてください。", [Source("manual", "Windows")]) with
        {
            FactResolution = new FactResolutionResult
            {
                Conflicts = ["version conflict"]
            }
        };

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException());

        Assert.NotEqual(AnswerReadiness.CustomerReady, result.Readiness);
    }

    private static AnswerDraftRequest Request(string inquiry, IReadOnlyList<SearchSource> sources) => new()
    {
        Case = new CaseContext { ProductName = "HelixQAC" },
        InquiryText = inquiry,
        Sources = sources,
        Settings = new AiAssistantSettings { MaxEvidenceItems = 5 }
    };

    private static SearchSource Source(string id, string text) => new()
    {
        SourceId = id,
        SourceType = "Manual",
        Title = "QAC Manual",
        Text = text,
        Score = 0.9
    };
}
