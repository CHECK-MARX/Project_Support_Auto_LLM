using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class ManualDocumentClassificationRegressionTests
{
    public static TheoryData<string, string, ManualDocumentCategory> ClassificationCases => new()
    {
        {
            "Perforce_QAC_Installation_Notes.pdf",
            "# Installation Notes\nPS C:\\Work> setup.exe /quiet\nPS C:\\Work> configure.exe --apply\nConfirm that installation completed successfully.",
            ManualDocumentCategory.ImportCandidate
        },
        {
            "Perforce-QAC-VisualStudio-Manual.pdf",
            "# Visual Studio Manual\nUse qacli.exe analyze to start analysis.\nThe command runs for the selected Visual Studio project.",
            ManualDocumentCategory.ImportCandidate
        },
        {
            "Helix QAC ライセンス設定（rlm）手順書 - Windows.docx",
            "# ライセンス設定手順\n次のPowerShellコマンドを実行してください。\nPS C:\\Work> rlmutil.exe rlmstat\n表示されたライセンスサーバー名を確認します。",
            ManualDocumentCategory.ImportCandidate
        },
        {
            "session_trace.txt",
            "2026-08-09 10:00:00 TRACE start\n2026-08-09 10:00:01 TRACE running\n2026-08-09 10:00:02 ERROR failed",
            ManualDocumentCategory.ContentExcludedText
        },
        {
            "build_log.txt",
            "2026-08-09 10:00:00 INFO build started\n2026-08-09 10:00:01 ERROR build failed\n2026-08-09 10:00:02 INFO build ended",
            ManualDocumentCategory.ContentExcludedText
        },
        {
            "console_output.txt",
            "Windows PowerShell Copyright (C) Microsoft Corporation. All rights reserved.\nPS C:\\Work> tool.exe start\nPS C:\\Work> tool.exe status",
            ManualDocumentCategory.ContentExcludedText
        },
        {
            "procedure.txt",
            "# Procedure\nRun the following command after saving the project.\nPS C:\\Work> qacli.exe analyze\nThe analysis result is written to the project output folder.",
            ManualDocumentCategory.ImportCandidate
        },
        {
            "support_log_設定手順書.md",
            "# 設定手順書\nログ出力を有効にするには次の設定を行います。\nPS C:\\Work> support.exe --enable-log\n設定後にサービスを再起動してください。",
            ManualDocumentCategory.ImportCandidate
        },
        {
            "operations_manual.txt",
            "2026-08-09 10:00:00 INFO start\n2026-08-09 10:00:01 WARN retry\n2026-08-09 10:00:02 ERROR failed\n2026-08-09 10:00:03 INFO end",
            ManualDocumentCategory.ContentExcludedText
        },
    };

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void ClassifyTextFileContent_DistinguishesManualsFromExecutionOutput(
        string fileName,
        string content,
        ManualDocumentCategory expectedCategory)
    {
        var result = ManualDocumentFilter.ClassifyTextFileContent(Path.Combine(@"D:\Manuals", fileName), content);

        Assert.Equal(expectedCategory, result.Category);
        Assert.NotNull(result.Scores);
        Assert.Contains("ManualScore=", result.Reason, StringComparison.Ordinal);
        Assert.Contains("LogScore=", result.Reason, StringComparison.Ordinal);
    }
}
