using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Drafts;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Drafts;

public class AiDraftStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 2, 17, 15, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task SaveAsync_SavesAnswerDraftResultUnderAiDataDrafts()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiDraftStore(() => FixedNow);

        var savedPath = await store.SaveAsync(CreateRequest(aiDataFolder), CreateResult());

        Assert.True(File.Exists(savedPath));
        Assert.Equal(System.IO.Path.Combine(aiDataFolder, "drafts"), System.IO.Path.GetDirectoryName(savedPath));
        Assert.EndsWith("_00001234_answer-draft.json", savedPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_CreatesDraftDirectoryWhenMissing()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var draftsFolder = System.IO.Path.Combine(aiDataFolder, "drafts");
        var store = new AiDraftStore(() => FixedNow);

        _ = await store.SaveAsync(CreateRequest(aiDataFolder), CreateResult());

        Assert.True(Directory.Exists(draftsFolder));
    }

    [Fact]
    public async Task SaveAsync_WritesExpectedDraftJsonContent()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var store = new AiDraftStore(() => FixedNow);

        var savedPath = await store.SaveAsync(CreateRequest(aiDataFolder), CreateResult());

        await using var stream = File.OpenRead(savedPath);
        var document = await JsonSerializer.DeserializeAsync<AiDraftDocument>(stream);
        Assert.NotNull(document);
        Assert.Equal(FixedNow, document.SavedAt);
        Assert.Equal("問い合わせ本文", document.InquiryText);
        Assert.Equal("お客様向け回答案", document.CustomerReplyDraft);
        Assert.Equal("社内メモ", document.InternalMemo);
        Assert.Single(document.NeedConfirmations);
        Assert.Single(document.Evidence);
        Assert.Equal(0.74, document.Confidence);
        Assert.Single(document.Warnings);
        Assert.Equal("Ollama", document.Provider.Provider);
        Assert.Equal("qwen3:14b", document.Provider.ChatModel);
        Assert.Equal(8192, document.Provider.ContextWindowTokens);
    }

    [Fact]
    public async Task SaveAsync_DoesNotSaveApiKeyValue()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var apiKey = "sk-1234567890abcdef";
        var request = CreateRequest(aiDataFolder) with
        {
            InquiryText = $"問い合わせ本文 {apiKey}",
            Settings = new AiAssistantSettings
            {
                AiDataFolder = aiDataFolder,
                LlmProvider = new LlmProviderSettings
                {
                    Provider = "Ollama",
                    Endpoint = $"http://localhost:11434?api_key={apiKey}",
                    ChatModel = "llama3.1",
                    ApiKeyEnvironmentVariable = "SUPPORT_AI_API_KEY",
                },
            },
        };
        var result = CreateResult() with
        {
            InternalMemo = $"Bearer {apiKey}",
            Warnings = [$"api_key={apiKey}"],
        };
        var store = new AiDraftStore(() => FixedNow);

        var savedPath = await store.SaveAsync(request, result);

        var json = await File.ReadAllTextAsync(savedPath);
        Assert.DoesNotContain(apiKey, json);
        Assert.Contains("SUPPORT_AI_API_KEY", json);
    }

    [Fact]
    public async Task SaveAsync_UsesSafeFileName()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var request = CreateRequest(aiDataFolder) with
        {
            Case = CreateRequest(aiDataFolder).Case with
            {
                SupportNumber = "00:00/12*34?",
            },
        };
        var store = new AiDraftStore(() => FixedNow);

        var savedPath = await store.SaveAsync(request, CreateResult());
        var fileName = System.IO.Path.GetFileName(savedPath);

        Assert.DoesNotContain(System.IO.Path.GetInvalidFileNameChars(), ch => fileName.Contains(ch));
        Assert.EndsWith("_00_00_12_34_answer-draft.json", fileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_DoesNotModifyExistingCaseFolderOrNote()
    {
        using var temp = new TempDirectory();
        var aiDataFolder = System.IO.Path.Combine(temp.Path, "ai-data");
        var caseFolder = System.IO.Path.Combine(temp.Path, "case-folder");
        Directory.CreateDirectory(caseFolder);
        var notePath = System.IO.Path.Combine(caseFolder, "note.txt");
        await File.WriteAllTextAsync(notePath, "既存ノート");
        var expectedLastWriteTime = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(notePath, expectedLastWriteTime);
        var request = CreateRequest(aiDataFolder) with
        {
            Case = CreateRequest(aiDataFolder).Case with
            {
                CaseFolderPath = caseFolder,
            },
            Sources =
            [
                new SearchSource
                {
                    SourceId = "source-1",
                    SourceType = "PastCase",
                    Title = "既存ノート",
                    Text = "根拠本文",
                    FilePath = notePath,
                    Score = 0.8,
                },
            ],
        };
        var store = new AiDraftStore(() => FixedNow);

        _ = await store.SaveAsync(request, CreateResult());

        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(notePath));
        Assert.Equal("既存ノート", await File.ReadAllTextAsync(notePath));
        Assert.Single(Directory.GetFiles(caseFolder));
    }

    private static AnswerDraftRequest CreateRequest(string aiDataFolder)
    {
        return new AnswerDraftRequest
        {
            Case = new CaseContext
            {
                ProductName = "製品A",
                CompanyName = "株式会社サンプル",
                CustomerName = "山田 太郎",
                SupportNumber = "00001234",
                Status = "対応中",
                ReceptionDate = new DateOnly(2026, 6, 2),
            },
            InquiryText = "問い合わせ本文",
            Sources =
            [
                new SearchSource
                {
                    SourceId = "source-1",
                    SourceType = "PastCase",
                    Title = "類似案件",
                    Text = "根拠本文",
                    Score = 0.8,
                },
            ],
            Settings = new AiAssistantSettings
            {
                AiDataFolder = aiDataFolder,
                LlmProvider = new LlmProviderSettings
                {
                    Provider = "Ollama",
                    Endpoint = "http://localhost:11434",
                    ChatModel = "qwen3:14b",
                },
            },
        };
    }

    private static AnswerDraftResult CreateResult()
    {
        return new AnswerDraftResult
        {
            CustomerReplyDraft = "お客様向け回答案",
            InternalMemo = "社内メモ",
            NeedConfirmations =
            [
                new NeedConfirmationItem
                {
                    Question = "対象バージョンを確認してください。",
                    Reason = "根拠に条件があります。",
                    Priority = "High",
                    RelatedSourceIds = ["source-1"],
                },
            ],
            Evidence =
            [
                new EvidenceItem
                {
                    SourceId = "source-1",
                    SourceType = "PastCase",
                    Title = "類似案件",
                    Excerpt = "根拠本文",
                    Relevance = 0.8,
                },
            ],
            Confidence = 0.74,
            Warnings = ["根拠不足のため要確認"],
            GeneratedAt = FixedNow,
        };
    }
}
