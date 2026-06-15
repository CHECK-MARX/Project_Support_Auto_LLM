using System.Text.Json;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Tests.Contracts;

public class DtoJsonTests
{
    [Fact]
    public void CaseContext_SerializesToJson()
    {
        var context = new CaseContext
        {
            Source = "AiAssistant.App",
            ProductName = "製品A",
            BaseFolder = @"D:\Support\Cases",
            CloseFolder = @"D:\Support\Closed",
            CaseFolderPath = @"D:\Support\Cases\SUP-001",
            CompanyName = "株式会社サンプル",
            CustomerName = "山田 太郎",
            SupportNumber = "SUP-001",
            Status = "対応中",
            ReceptionDate = new DateOnly(2026, 6, 2),
            SelectedText = "選択テキスト",
            Notes =
            [
                new NoteSnapshot
                {
                    NoteKind = "対応メモ",
                    FilePath = @"D:\Support\Cases\SUP-001\note.txt",
                    FileName = "note.txt",
                    Text = "日本語のノート本文",
                    LastModifiedAt = new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.FromHours(9)),
                    IsCurrent = true,
                },
            ],
        };

        var json = JsonSerializer.Serialize(context);

        Assert.Contains("\"supportNumber\"", json);
        Assert.Contains("\"notes\"", json);
        Assert.Contains("D:\\\\Support\\\\Cases", json);
    }

    [Fact]
    public void CaseContext_DeserializesFromJson()
    {
        var json = """
            {
              "source": "SupportCaseManager.App",
              "productName": "製品A",
              "baseFolder": "D:\\Support\\Cases",
              "closeFolder": "D:\\Support\\Closed",
              "caseFolderPath": "D:\\Support\\Cases\\SUP-001",
              "companyName": "株式会社サンプル",
              "customerName": "山田 太郎",
              "supportNumber": "SUP-001",
              "status": "対応中",
              "receptionDate": "2026-06-02",
              "selectedText": "選択テキスト",
              "notes": [
                {
                  "noteKind": "対応メモ",
                  "filePath": "D:\\Support\\Cases\\SUP-001\\note.txt",
                  "fileName": "note.txt",
                  "text": "日本語のノート本文",
                  "lastModifiedAt": "2026-06-02T10:00:00+09:00",
                  "isCurrent": true
                }
              ]
            }
            """;

        var context = JsonSerializer.Deserialize<CaseContext>(json);

        Assert.NotNull(context);
        Assert.Equal("SupportCaseManager.App", context.Source);
        Assert.Equal("製品A", context.ProductName);
        Assert.Equal(@"D:\Support\Cases", context.BaseFolder);
        Assert.Equal("株式会社サンプル", context.CompanyName);
        Assert.Equal("SUP-001", context.SupportNumber);
        Assert.Equal(new DateOnly(2026, 6, 2), context.ReceptionDate);
        Assert.Single(context.Notes);
        Assert.Equal("日本語のノート本文", context.Notes[0].Text);
    }

    [Fact]
    public void AnswerDraftRequest_SerializesToJson()
    {
        var request = new AnswerDraftRequest
        {
            Case = new CaseContext
            {
                ProductName = "製品A",
                SupportNumber = "SUP-001",
                Notes = [],
            },
            InquiryText = "エラーの原因を確認したいです。",
            UserInstruction = "簡潔に回答してください。",
            Sources =
            [
                new SearchSource
                {
                    SourceId = "case:SUP-000:note",
                    SourceType = "PastCase",
                    Title = "類似案件",
                    Text = "過去案件の抜粋",
                    FilePath = @"D:\Support\Closed\SUP-000\note.txt",
                    SupportNumber = "SUP-000",
                    Score = 0.84,
                },
            ],
            Settings = new AiAssistantSettings
            {
                AiDataFolder = @"D:\Support\ai-data",
                AiIndexFolder = @"D:\Support\ai-index",
            },
            RequestedAt = new DateTimeOffset(2026, 6, 2, 10, 30, 0, TimeSpan.FromHours(9)),
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"case\"", json);
        Assert.Contains("\"inquiryText\"", json);
        Assert.Contains("\"sources\"", json);
        Assert.Contains("\"settings\"", json);
        Assert.Contains("D:\\\\Support\\\\ai-data", json);
    }

    [Fact]
    public void AnswerDraftResult_DeserializesFromJson()
    {
        var json = """
            {
              "customerReplyDraft": "確認できる範囲では、設定の見直しが必要です。",
              "internalMemo": "case:SUP-000:note を参照。",
              "needConfirmations": [
                {
                  "question": "対象バージョンを確認してください。",
                  "reason": "根拠資料にバージョン条件があります。",
                  "priority": "High",
                  "relatedSourceIds": ["manual:install:001"]
                }
              ],
              "evidence": [
                {
                  "sourceId": "manual:install:001",
                  "sourceType": "Manual",
                  "title": "インストール手順",
                  "excerpt": "設定後に再起動してください。",
                  "filePath": "D:\\Manuals\\install.txt",
                  "supportNumber": null,
                  "relevance": 0.91
                }
              ],
              "confidence": 0.72,
              "warnings": ["根拠不足のため断定不可"],
              "generatedAt": "2026-06-02T10:31:00+09:00"
            }
            """;

        var result = JsonSerializer.Deserialize<AnswerDraftResult>(json);

        Assert.NotNull(result);
        Assert.Equal("確認できる範囲では、設定の見直しが必要です。", result.CustomerReplyDraft);
        Assert.Equal("case:SUP-000:note を参照。", result.InternalMemo);
        Assert.Single(result.NeedConfirmations);
        Assert.Equal("High", result.NeedConfirmations[0].Priority);
        Assert.Single(result.Evidence);
        Assert.Equal("manual:install:001", result.Evidence[0].SourceId);
        Assert.Equal(0.72, result.Confidence);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void AiAssistantSettings_DefaultValuesAreExpected()
    {
        var settings = new AiAssistantSettings();

        Assert.Equal(string.Empty, settings.AiDataFolder);
        Assert.Equal(string.Empty, settings.AiIndexFolder);
        Assert.Null(settings.BaseFolder);
        Assert.Null(settings.CloseFolder);
        Assert.Null(settings.DefaultProductName);
        Assert.Equal(8, settings.MaxEvidenceItems);
        Assert.Equal(0.65, settings.AutoSelectMinimumScore);
        Assert.Equal(0, settings.MinimumDisplayScore);
        Assert.Equal(24000, settings.MaxPromptChars);
        Assert.False(settings.EnableCloudLlm);
        Assert.True(settings.MaskSensitiveDataForCloud);
        Assert.Equal("Ollama", settings.LlmProvider.Provider);
        Assert.Equal("http://localhost:11434", settings.LlmProvider.Endpoint);
        Assert.Equal("llama3.1", settings.LlmProvider.ChatModel);
        Assert.Null(settings.LlmProvider.EmbeddingModel);
        Assert.Equal(120, settings.LlmProvider.TimeoutSeconds);
        Assert.Equal(0.2, settings.LlmProvider.Temperature);
        Assert.Equal(2048, settings.LlmProvider.MaxOutputTokens);
        Assert.Null(settings.LlmProvider.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public void JapaneseTextWindowsPathsAndEmptyLists_RoundTrip()
    {
        var context = new CaseContext
        {
            ProductName = "製品A",
            BaseFolder = @"D:\サポート\案件",
            CloseFolder = @"D:\サポート\クローズ",
            CompanyName = "株式会社サンプル",
            SupportNumber = "SUP-001",
            Notes = [],
        };

        var json = JsonSerializer.Serialize(context);
        var restored = JsonSerializer.Deserialize<CaseContext>(json);

        Assert.NotNull(restored);
        Assert.Equal("製品A", restored.ProductName);
        Assert.Equal(@"D:\サポート\案件", restored.BaseFolder);
        Assert.Equal(@"D:\サポート\クローズ", restored.CloseFolder);
        Assert.Equal("株式会社サンプル", restored.CompanyName);
        Assert.NotNull(restored.Notes);
        Assert.Empty(restored.Notes);
    }
}
