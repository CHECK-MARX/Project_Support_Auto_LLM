using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class ManualDocumentFilterTests
{
    [Fact]
    public void ClassifyTextFileContent_ExcludesPowerShellLogText()
    {
        var text = """
            Windows PowerShell Copyright (C) Microsoft Corporation. All rights reserved.
            PS C:\Work> dotnet build
            PS C:\Work> dotnet test
            """;

        var result = ManualDocumentFilter.ClassifyTextFileContent(@"D:\Manuals\session.txt", text);

        Assert.Equal(ManualDocumentCategory.ContentExcludedText, result.Category);
    }

    [Theory]
    [InlineData("build_output.txt")]
    [InlineData("command_result.md")]
    [InlineData("debug_trace.txt")]
    [InlineData("support_log.md")]
    public void ClassifyTextFileContent_ExcludesCommandBuildLogFileNames(string fileName)
    {
        var result = ManualDocumentFilter.ClassifyTextFileContent(
            Path.Combine(@"D:\Manuals", fileName),
            "# 手順書\r\n通常の説明文です。");

        Assert.Equal(ManualDocumentCategory.ContentExcludedText, result.Category);
    }

    [Fact]
    public void ClassifyTextFileContent_ImportsNormalTextManual()
    {
        var result = ManualDocumentFilter.ClassifyTextFileContent(
            @"D:\Manuals\license_manual.txt",
            "ライセンス認証エラーの場合はライセンスサーバー名とポート番号を確認します。");

        Assert.Equal(ManualDocumentCategory.ImportCandidate, result.Category);
    }

    [Fact]
    public void ClassifyTextFileContent_ImportsMarkdownManual()
    {
        var result = ManualDocumentFilter.ClassifyTextFileContent(
            @"D:\Manuals\license_manual.md",
            "# ライセンス認証エラー対応手順\r\nファイアウォール設定を確認します。");

        Assert.Equal(ManualDocumentCategory.ImportCandidate, result.Category);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".run")]
    [InlineData(".db")]
    [InlineData(".pdb")]
    [InlineData(".bak")]
    [InlineData(".zip")]
    public void ClassifyFile_CountsOutOfScopeBinaryOrArchiveExtensions(string extension)
    {
        var result = ManualDocumentFilter.ClassifyFile($@"D:\Manuals\tool{extension}");

        Assert.Equal(ManualDocumentCategory.OutOfScopeBinaryOrArchive, result.Category);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".png")]
    public void ClassifyFile_CountsUnsupportedDocumentFormats(string extension)
    {
        var result = ManualDocumentFilter.ClassifyFile($@"D:\Manuals\manual{extension}");

        Assert.Equal(ManualDocumentCategory.UnsupportedDocumentFormat, result.Category);
    }
}
