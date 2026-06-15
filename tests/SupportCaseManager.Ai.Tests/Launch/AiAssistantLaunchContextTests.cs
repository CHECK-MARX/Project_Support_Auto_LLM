using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Launch;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Launch;

public sealed class AiAssistantLaunchContextTests
{
    [Fact]
    public async Task ReadAsync_ReadsLaunchContextFromJson()
    {
        using var temp = new TempDirectory();
        var contextFilePath = Path.Combine(temp.Path, "ai-context.json");
        await File.WriteAllTextAsync(
            contextFilePath,
            """
            {
              "source": "SupportCaseManager.App",
              "productName": "Klocwork",
              "baseFolder": "C:\\Support\\Klocwork\\Open",
              "closeFolder": "C:\\Support\\Klocwork\\Closed",
              "caseFolderPath": "C:\\Support\\Klocwork\\Open\\20260602(company_00017581)",
              "companyName": "日本語株式会社",
              "customerName": "山田 太郎",
              "supportNumber": "00017581",
              "status": "お客様へ返信中",
              "receptionDate": "2026-06-02",
              "noteKind": "お客様への返信案",
              "noteFilePath": "C:\\Support\\Klocwork\\Open\\20260602(company_00017581)\\reply_00017581.txt",
              "selectedText": "選択テキスト",
              "currentNoteText": "既存ノート本文",
              "inquiryText": "ライセンス認証エラーです。",
              "additionalInstruction": "丁寧に回答してください。"
            }
            """,
            Encoding.UTF8);
        var reader = new AiAssistantLaunchContextReader();

        var context = await reader.ReadAsync(contextFilePath);

        Assert.Equal("SupportCaseManager.App", context.Source);
        Assert.Equal("Klocwork", context.ProductName);
        Assert.Equal(@"C:\Support\Klocwork\Open", context.BaseFolder);
        Assert.Equal("日本語株式会社", context.CompanyName);
        Assert.Equal("00017581", context.SupportNumber);
        Assert.Equal(new DateOnly(2026, 6, 2), context.ReceptionDate);
        Assert.Equal("既存ノート本文", context.CurrentNoteText);
        Assert.Equal("丁寧に回答してください。", context.AdditionalInstruction);
    }

    [Fact]
    public async Task ReadAsync_IgnoresUnknownProperties()
    {
        using var temp = new TempDirectory();
        var contextFilePath = Path.Combine(temp.Path, "ai-context.json");
        await File.WriteAllTextAsync(
            contextFilePath,
            """
            {
              "source": "SupportCaseManager.App",
              "productName": "HelixQAC",
              "unknownProperty": "ignored"
            }
            """,
            Encoding.UTF8);
        var reader = new AiAssistantLaunchContextReader();

        var context = await reader.ReadAsync(contextFilePath);

        Assert.Equal("SupportCaseManager.App", context.Source);
        Assert.Equal("HelixQAC", context.ProductName);
    }

    [Fact]
    public async Task ReadAsync_MissingFileFailsClearly()
    {
        using var temp = new TempDirectory();
        var reader = new AiAssistantLaunchContextReader();

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            reader.ReadAsync(Path.Combine(temp.Path, "missing-ai-context.json")));

        Assert.Contains("context file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_InvalidJsonFailsClearly()
    {
        using var temp = new TempDirectory();
        var contextFilePath = Path.Combine(temp.Path, "ai-context.json");
        await File.WriteAllTextAsync(contextFilePath, "{ invalid json", Encoding.UTF8);
        var reader = new AiAssistantLaunchContextReader();

        await Assert.ThrowsAnyAsync<JsonException>(() => reader.ReadAsync(contextFilePath));
    }

    [Fact]
    public async Task ReadAsync_DoesNotModifyContextFile()
    {
        using var temp = new TempDirectory();
        var contextFilePath = Path.Combine(temp.Path, "ai-context.json");
        var json = """
            {
              "source": "SupportCaseManager.App",
              "productName": "Klocwork"
            }
            """;
        await File.WriteAllTextAsync(contextFilePath, json, Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(contextFilePath, expectedLastWriteTime);
        var reader = new AiAssistantLaunchContextReader();

        _ = await reader.ReadAsync(contextFilePath);

        Assert.Equal(json, await File.ReadAllTextAsync(contextFilePath, Encoding.UTF8));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(contextFilePath));
    }

    [Fact]
    public void AiAssistantLaunchContext_DefaultValuesAreSafe()
    {
        var context = new AiAssistantLaunchContext();

        Assert.Equal(string.Empty, context.Source);
        Assert.Equal(string.Empty, context.ProductName);
        Assert.Null(context.ReceptionDate);
        Assert.Equal(string.Empty, context.CurrentNoteText);
        Assert.Equal(string.Empty, context.InquiryText);
    }
}
