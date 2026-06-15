using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Facts;

public sealed class GoldenQuestionFactTests
{
    [Fact]
    public void QuestionClassifier_ClassifiesLatestVersionQuestion()
    {
        var classification = new QuestionClassifier().Classify("現在のCxSAST最新バージョンは何でしょうか？EP、HFの最新バージョンも教えてください。");

        Assert.Contains(QuestionTypes.LatestVersionQuestion, classification.QuestionTypes);
        Assert.Contains(FactKeys.LatestSastVersion, classification.RequestedFacts);
        Assert.Contains(FactKeys.LatestEnginePackVersion, classification.RequestedFacts);
        Assert.Contains(FactKeys.LatestHotfixVersion, classification.RequestedFacts);
    }

    [Fact]
    public void QuestionClassifier_SeparatesCurrentInstalledVersionFromLatestRequestedFacts()
    {
        var classification = new QuestionClassifier().Classify("現在SAST 9.7.4 HF2を利用中です。最新SAST、EP、HFを教えてください。");

        Assert.Equal("SAST 9.7.4 HF2", classification.CurrentInstalledVersion);
        Assert.Contains(FactKeys.LatestSastVersion, classification.RequestedFacts);
        Assert.Contains(FactKeys.LatestEnginePackVersion, classification.RequestedFacts);
        Assert.Contains(FactKeys.LatestHotfixVersion, classification.RequestedFacts);
        Assert.DoesNotContain(FactKeys.UpgradePossibility, classification.RequestedFacts);
    }

    [Fact]
    public void QuestionClassifier_ClassifiesUpgradePossibilitySeparately()
    {
        var classification = new QuestionClassifier().Classify("SAST 9.7.4 HF2から最新へアップグレード可能ですか？");

        Assert.Contains(QuestionTypes.UpgradePossibilityQuestion, classification.QuestionTypes);
        Assert.Contains(FactKeys.UpgradePossibility, classification.RequestedFacts);
        Assert.Contains(FactKeys.CurrentInstalledVersion, classification.RequestedFacts);
        Assert.Equal("SAST 9.7.4 HF2", classification.CurrentInstalledVersion);
    }

    [Fact]
    public void OfficialDocumentFactExtractor_ExtractsVersionFacts()
    {
        var document = CreateOfficialDocument();

        var facts = new OfficialDocumentFactExtractor().Extract(document);

        Assert.Contains(facts, fact => fact.Key == FactKeys.LatestSastVersion && fact.Value == "9.7.0");
        Assert.Contains(facts, fact => fact.Key == FactKeys.LatestEnginePackVersion && fact.Value == "9.7.6");
        Assert.Contains(facts, fact => fact.Key == FactKeys.LatestHotfixVersion && fact.Value == "HF10");
        Assert.All(facts, fact => Assert.Equal("OfficialDoc", fact.SourceType));
    }

    [Fact]
    public void OfficialDocumentFactExtractor_DoesNotExtractSastLatestFromEnginePackOrScaPages()
    {
        var builtAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9));
        var document = new AiOfficialDocumentIndexDocument
        {
            ProductName = "Checkmarx",
            BuiltAt = builtAt,
            Documents =
            [
                new AiIndexedOfficialDocument
                {
                    Id = "engine-pack",
                    ProductName = "Checkmarx",
                    Url = "https://docs.example.test/engine-pack-version-9-7-6.html",
                    Title = "Engine Pack Version 9.7.6",
                    SectionTitle = "Engine Pack",
                    Text = "Engine Pack Version 9.7.6 for CxSAST.",
                    RetrievedAt = builtAt,
                    ContentHash = "engine-pack",
                },
                new AiIndexedOfficialDocument
                {
                    Id = "sca-release",
                    ProductName = "Checkmarx",
                    Url = "https://docs.example.test/checkmarx-sca/release-notes-3-0-0.html",
                    Title = "Checkmarx SCA Release Notes for 3.0.0",
                    SectionTitle = "Release Notes",
                    Text = "Checkmarx SCA 3.0.0 Release Notes. HF19.",
                    RetrievedAt = builtAt,
                    ContentHash = "sca-release",
                },
            ],
        };

        var facts = new OfficialDocumentFactExtractor().Extract(document);

        Assert.DoesNotContain(facts, fact => fact.Key == FactKeys.LatestSastVersion);
        Assert.DoesNotContain(facts, fact => fact.Value == "HF19");
        Assert.Contains(facts, fact => fact.Key == FactKeys.LatestEnginePackVersion && fact.Value == "9.7.6");
    }

    [Fact]
    public async Task FactResolver_ResolvesLatestVersionsFromOfficialIndex()
    {
        using var temp = new TempDirectory();
        await WriteOfficialIndexAsync(temp.Path, "Checkmarx");

        var result = new FactResolver().Resolve(
            "Checkmarx",
            temp.Path,
            "現在のCxSAST最新バージョンは何でしょうか？EP、HFの最新バージョンも教えてください。");

        Assert.Equal(AnswerReadiness.AutoAnswerable, result.AnswerReadiness);
        AssertFact(result, FactKeys.LatestSastVersion, "9.7.0");
        AssertFact(result, FactKeys.LatestEnginePackVersion, "9.7.6");
        AssertFact(result, FactKeys.LatestHotfixVersion, "HF10");
        Assert.True(result.LlmPromptUsesResolvedFacts);
    }

    [Fact]
    public async Task FactResolver_PrioritizesCuratedFactsOverWrongCrawlerCandidates()
    {
        using var temp = new TempDirectory();
        await CuratedFactCatalogStore.SaveAsync(
            temp.Path,
            new CuratedFactCatalog
            {
                ProductName = "Checkmarx",
                LatestSastVersion = "9.7.0",
                LatestEnginePackVersion = "9.7.6",
                LatestHotfixVersion = "HF10",
                SourceUrls =
                [
                    "https://docs.checkmarx.com/en/34965-321884-release-notes-for-9-7-0.html",
                    "https://docs.checkmarx.com/en/34965-591177-engine-pack-version-9-7-6.html",
                    "https://docs.checkmarx.com/en/34965-337700-hotfixes-9-7-0.html",
                ],
                UpdatedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.FromHours(9)),
            });
        await WriteWrongOfficialIndexAsync(temp.Path, "Checkmarx");

        var result = new FactResolver().Resolve(
            "Checkmarx",
            temp.Path,
            "現在のCxSAST最新バージョンは何でしょうか？EP、HFの最新バージョンも教えてください。");

        Assert.Equal(AnswerReadiness.AutoAnswerable, result.AnswerReadiness);
        AssertFact(result, FactKeys.LatestSastVersion, "9.7.0", "Curated");
        AssertFact(result, FactKeys.LatestEnginePackVersion, "9.7.6", "Curated");
        AssertFact(result, FactKeys.LatestHotfixVersion, "HF10", "Curated");
        Assert.Contains(result.CrawlerConflicts, conflict => conflict.Contains("LatestHotfixVersion", StringComparison.Ordinal) && conflict.Contains("HF19", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Conflicts, conflict => conflict == FactKeys.LatestHotfixVersion);

        var answerService = CreateAnswerService();
        var answer = await answerService.GenerateDraftAsync(CreateRequest(result));

        Assert.Contains("9.7.0", answer.CustomerReplyDraft);
        Assert.Contains("9.7.6", answer.CustomerReplyDraft);
        Assert.Contains("HF10", answer.CustomerReplyDraft);
        Assert.Contains("Crawler conflicts are diagnostics only", answer.InternalMemo);
    }

    [Fact]
    public void FactResolver_UsesBuiltInCheckmarxCuratedFactsWhenCatalogFileIsMissing()
    {
        using var temp = new TempDirectory();

        var result = new FactResolver().Resolve(
            "製品A",
            temp.Path,
            "CxSASTの最新バージョン教えてください。EPとHFの最新バージョンも教えてください。");

        Assert.Equal(AnswerReadiness.AutoAnswerable, result.AnswerReadiness);
        AssertFact(result, FactKeys.LatestSastVersion, "9.7.0", "Curated");
        AssertFact(result, FactKeys.LatestEnginePackVersion, "9.7.6", "Curated");
        AssertFact(result, FactKeys.LatestHotfixVersion, "HF10", "Curated");
    }

    [Fact]
    public async Task FactResolver_PrioritizesUserConfirmedFactsOverOfficialDocCandidatesWhenCuratedIsMissing()
    {
        using var temp = new TempDirectory();
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(temp.Path, "Checkmarx");
        var updatedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.FromHours(9));
        var catalog = FactCatalogStore.BuildCatalog(
            "Checkmarx",
            [
                new CandidateFact
                {
                    Key = FactKeys.LatestSastVersion,
                    Value = "9.7.0",
                    Confidence = FactConfidences.High,
                    SourceType = "UserConfirmed",
                    SourceUrl = "user-confirmed://checkmarx/latest-sast",
                    Title = "User confirmed latest CxSAST",
                    ExtractedAt = updatedAt,
                    Reason = "Support owner confirmed value.",
                },
                new CandidateFact
                {
                    Key = FactKeys.LatestSastVersion,
                    Value = "9.7.6",
                    Confidence = FactConfidences.High,
                    SourceType = "OfficialDoc",
                    SourceUrl = "https://docs.example.test/engine-pack-version-9-7-6.html",
                    Title = "Engine Pack Version 9.7.6",
                    ExtractedAt = updatedAt,
                    Reason = "Wrong crawler candidate.",
                },
            ],
            updatedAt);
        await FactCatalogStore.SaveAsync(productFolder, catalog);

        var result = new FactResolver().Resolve(
            "Checkmarx",
            temp.Path,
            "What is the latest CxSAST version?");

        AssertFact(result, FactKeys.LatestSastVersion, "9.7.0", "UserConfirmed");
        Assert.DoesNotContain(result.Conflicts, conflict => conflict == FactKeys.LatestSastVersion);
    }

    [Fact]
    public async Task FactResolver_DoesNotUseCurrentInstalledVersionAsLatestTarget()
    {
        using var temp = new TempDirectory();
        await WriteOfficialIndexAsync(temp.Path, "Checkmarx");

        var result = new FactResolver().Resolve(
            "Checkmarx",
            temp.Path,
            "現在SAST 9.7.4 HF2を利用中です。最新SAST、EP、HFを教えてください。");

        Assert.Equal("SAST 9.7.4 HF2", result.Classification.CurrentInstalledVersion);
        AssertFact(result, FactKeys.LatestSastVersion, "9.7.0");
        AssertFact(result, FactKeys.LatestEnginePackVersion, "9.7.6");
        AssertFact(result, FactKeys.LatestHotfixVersion, "HF10");
    }

    [Fact]
    public async Task FactResolver_SeparatesCurrentInstalledVersionForUpgradeQuestionAndUsesCuratedLatestFacts()
    {
        using var temp = new TempDirectory();
        await CuratedFactCatalogStore.SaveAsync(
            temp.Path,
            new CuratedFactCatalog
            {
                ProductName = "Checkmarx",
                LatestSastVersion = "9.7.0",
                LatestEnginePackVersion = "9.7.6",
                LatestHotfixVersion = "HF10",
                UpdatedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.FromHours(9)),
            });

        var result = new FactResolver().Resolve(
            "Checkmarx",
            temp.Path,
            "現在利用中のCheckmarx SAST（CxSAST）9.7.4 HF2を最新バージョンにアップデート可能でしょうか？EPとHFの最新バージョンも教えてください。");

        Assert.Equal("SAST 9.7.4 HF2", result.Classification.CurrentInstalledVersion);
        Assert.Contains(FactKeys.LatestSastVersion, result.Classification.RequestedFacts);
        Assert.Contains(FactKeys.LatestEnginePackVersion, result.Classification.RequestedFacts);
        Assert.Contains(FactKeys.LatestHotfixVersion, result.Classification.RequestedFacts);
        Assert.Contains(FactKeys.UpgradePossibility, result.Classification.RequestedFacts);
        AssertFact(result, FactKeys.LatestSastVersion, "9.7.0", "Curated");
        AssertFact(result, FactKeys.LatestEnginePackVersion, "9.7.6", "Curated");
        AssertFact(result, FactKeys.LatestHotfixVersion, "HF10", "Curated");
        Assert.DoesNotContain(result.ResolvedFacts, fact => fact.Key == FactKeys.LatestSastVersion && fact.Value == "9.7.4");
        Assert.Equal(AnswerReadiness.NeedsConfirmation, result.AnswerReadiness);
    }

    [Fact]
    public async Task FactResolver_RequiresConfirmationForUpgradePossibilityWithoutOfficialPath()
    {
        using var temp = new TempDirectory();
        await WriteOfficialIndexAsync(temp.Path, "Checkmarx");

        var result = new FactResolver().Resolve(
            "Checkmarx",
            temp.Path,
            "SAST 9.7.4 HF2から最新へアップグレード可能ですか？");

        Assert.Contains(QuestionTypes.UpgradePossibilityQuestion, result.Classification.QuestionTypes);
        Assert.Equal(AnswerReadiness.NeedsConfirmation, result.AnswerReadiness);
        Assert.Contains(FactKeys.UpgradePossibility, result.MissingFacts);
    }

    [Fact]
    public void FactResolver_ReturnsInsufficientEvidenceWithoutOfficialFacts()
    {
        using var temp = new TempDirectory();

        var result = new FactResolver().Resolve(
            "UnknownProduct",
            temp.Path,
            "What is the latest version?");

        Assert.Equal(AnswerReadiness.InsufficientEvidence, result.AnswerReadiness);
        Assert.Contains(FactKeys.LatestSastVersion, result.MissingFacts);
    }

    [Fact]
    public async Task PromptBuilder_IncludesResolvedFacts()
    {
        using var temp = new TempDirectory();
        await WriteOfficialIndexAsync(temp.Path, "Checkmarx");
        var factResolution = new FactResolver().Resolve(
            "Checkmarx",
            temp.Path,
            "現在のCxSAST最新バージョンは何でしょうか？EP、HFの最新バージョンも教えてください。");
        var request = CreateRequest(factResolution);

        var messages = new PromptBuilder().Build(request);

        Assert.Contains("ResolvedFacts", messages.UserPrompt);
        Assert.Contains("アプリ側で確定済みの最新バージョン:", messages.UserPrompt);
        Assert.Contains("CxSAST: 9.7.0", messages.UserPrompt);
        Assert.Contains("Engine Pack: 9.7.6", messages.UserPrompt);
        Assert.Contains("Hotfix: HF10", messages.UserPrompt);
        Assert.Contains("LatestSastVersion: 9.7.0", messages.UserPrompt);
        Assert.Contains("LatestEnginePackVersion: 9.7.6", messages.UserPrompt);
        Assert.Contains("LatestHotfixVersion: HF10", messages.UserPrompt);
    }

    private static void AssertFact(FactResolutionResult result, string key, string expectedValue, string? expectedSourceType = null)
    {
        var fact = Assert.Single(result.ResolvedFacts, fact => fact.Key == key);
        Assert.Equal(expectedValue, fact.Value);
        Assert.Equal(FactStatuses.Confirmed, fact.Status);
        Assert.Equal(FactConfidences.High, fact.Confidence);
        if (expectedSourceType is not null)
        {
            Assert.Equal(expectedSourceType, fact.SourceType);
        }
    }

    private static AnswerDraftRequest CreateRequest(FactResolutionResult factResolution)
    {
        return new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "Checkmarx" },
            InquiryText = "現在のCxSAST最新バージョンは何でしょうか？EP、HFの最新バージョンも教えてください。",
            FactResolution = factResolution,
            Settings = new AiAssistantSettings(),
        };
    }

    private static AiAnswerService CreateAnswerService()
    {
        return new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new FakeLlmClient());
    }

    private static async Task WriteOfficialIndexAsync(string aiIndexFolder, string productName)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, CreateOfficialDocument(productName));
    }

    private static async Task WriteWrongOfficialIndexAsync(string aiIndexFolder, string productName)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        var builtAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9));
        await using var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = productName,
            BuiltAt = builtAt,
            Documents =
            [
                new AiIndexedOfficialDocument
                {
                    Id = "wrong-sast-engine-pack",
                    ProductName = productName,
                    Url = "https://docs.example.test/engine-pack-version-9-7-6.html",
                    Title = "Engine Pack Version 9.7.6",
                    SectionTitle = "Engine Pack",
                    Text = "Engine Pack Version 9.7.6 for CxSAST.",
                    RetrievedAt = builtAt,
                    ContentHash = "wrong-ep",
                },
                new AiIndexedOfficialDocument
                {
                    Id = "wrong-hf19",
                    ProductName = productName,
                    Url = "https://docs.example.test/hotfixes-9-8-0.html",
                    Title = "9.8.0 Hotfixes",
                    SectionTitle = "Hotfixes",
                    Text = "Latest Hotfix: HF19.",
                    RetrievedAt = builtAt,
                    ContentHash = "wrong-hf",
                },
            ],
        });
    }

    private static AiOfficialDocumentIndexDocument CreateOfficialDocument(string productName = "Checkmarx")
    {
        var builtAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9));
        return new AiOfficialDocumentIndexDocument
        {
            ProductName = productName,
            BuiltAt = builtAt,
            Documents =
            [
                new AiIndexedOfficialDocument
                {
                    Id = "official-sast-970",
                    ProductName = productName,
                    Url = "https://docs.example.test/release-notes-9-7-0.html",
                    Title = "Release Notes for 9.7.0",
                    SectionTitle = "Release Notes",
                    Text = "CxSAST 9.7.0 Release Notes.",
                    RetrievedAt = builtAt,
                    ContentHash = "hash-sast",
                },
                new AiIndexedOfficialDocument
                {
                    Id = "official-ep-976",
                    ProductName = productName,
                    Url = "https://docs.example.test/engine-pack-version-9-7-6.html",
                    Title = "Engine Pack Version 9.7.6",
                    SectionTitle = "Engine Pack",
                    Text = "Engine Pack Version 9.7.6 is the latest engine pack for CxSAST.",
                    RetrievedAt = builtAt,
                    ContentHash = "hash-ep",
                },
                new AiIndexedOfficialDocument
                {
                    Id = "official-hf10",
                    ProductName = productName,
                    Url = "https://docs.example.test/hotfixes-9-7-0.html",
                    Title = "9.7.0 Hotfixes",
                    SectionTitle = "Hotfixes",
                    Text = "Latest Hotfix: HF10. Previous hotfixes: HF9 HF8.",
                    RetrievedAt = builtAt,
                    ContentHash = "hash-hf",
                },
            ],
        };
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmGenerationResult
            {
                Content = """
                    {
                      "customerReplyDraft": "LLM placeholder",
                      "internalMemo": "LLM placeholder",
                      "needConfirmations": [],
                      "evidence": [],
                      "confidence": 0.1,
                      "warnings": []
                    }
                    """,
                DoneReason = "stop",
            });
        }
    }
}
