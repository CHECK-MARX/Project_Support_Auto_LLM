using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Indexing;

public static class ManualDocumentFilter
{
    private static readonly HashSet<string> SupportedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".text",
        ".md",
        ".markdown",
        ".csv",
        ".tsv",
        ".html",
        ".htm",
        ".rst",
        ".adoc",
        ".asciidoc",
        ".pdf",
        ".docx",
        ".xlsx",
        ".pptx",
    };

    private static readonly HashSet<string> UnsupportedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".tif",
        ".tiff",
        ".doc",
        ".xls",
        ".ppt",
    };

    private static readonly HashSet<string> OutOfScopeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".run",
        ".db",
        ".pdb",
        ".bak",
        ".zip",
    };

    private static readonly string[] ExcludedFileNameTokens =
    [
        "log",
        "trace",
        "debug",
        "build",
        "command",
    ];

    private static readonly string[] ExcludedContentMarkers =
    [
        "Windows PowerShell Copyright",
        "Copyright (C) Microsoft Corporation. All rights reserved.",
        "No framework installation found",
        "CCT_Generator.exe",
    ];

    private static readonly Regex PowerShellPromptRegex = new(@"(^|\r?\n)\s*PS\s+[A-Z]:\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DriveCommandPromptRegex = new(@"(^|\r?\n)\s*[A-Z]:\\[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExeCommandRegex = new(@"\b[\w.\-]+\.exe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ManualDocumentFilterResult ClassifyFile(string filePath)
    {
        var extension = NormalizeExtension(Path.GetExtension(filePath));
        if (SupportedDocumentExtensions.Contains(extension))
        {
            return new ManualDocumentFilterResult(ManualDocumentCategory.ImportCandidate, extension, string.Empty);
        }

        if (UnsupportedDocumentExtensions.Contains(extension))
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.UnsupportedDocumentFormat,
                extension,
                "Legacy Office formats and image-only documents are not imported. Use PDF/DOCX/XLSX/PPTX/HTML/TXT/MD/CSV/TSV.");
        }

        if (OutOfScopeExtensions.Contains(extension))
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.OutOfScopeBinaryOrArchive,
                extension,
                "Binary, database, backup, executable, or archive file is outside the manual search target.");
        }

        return new ManualDocumentFilterResult(
            ManualDocumentCategory.UnsupportedOther,
            extension,
            "Unsupported extension.");
    }

    public static ManualDocumentFilterResult ClassifyTextFileContent(string filePath, string text)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        if (ExcludedFileNameTokens.Any(token => fileName.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.ContentExcludedText,
                NormalizeExtension(Path.GetExtension(filePath)),
                "File name looks like a log, trace, debug, build, or command output file.");
        }

        if (ExcludedContentMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.ContentExcludedText,
                NormalizeExtension(Path.GetExtension(filePath)),
                "File content looks like PowerShell, build, command, or console output.");
        }

        if (PowerShellPromptRegex.Matches(text).Count >= 2 ||
            DriveCommandPromptRegex.Matches(text).Count >= 2 ||
            ExeCommandRegex.Matches(text).Count >= 3)
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.ContentExcludedText,
                NormalizeExtension(Path.GetExtension(filePath)),
                "File content contains repeated shell prompts or executable commands.");
        }

        return new ManualDocumentFilterResult(
            ManualDocumentCategory.ImportCandidate,
            NormalizeExtension(Path.GetExtension(filePath)),
            string.Empty);
    }

    public static string NormalizeExtension(string? extension)
    {
        return string.IsNullOrWhiteSpace(extension)
            ? "(none)"
            : extension.Trim().ToLowerInvariant();
    }
}

public enum ManualDocumentCategory
{
    ImportCandidate,
    ContentExcludedText,
    UnsupportedDocumentFormat,
    OutOfScopeBinaryOrArchive,
    UnsupportedOther,
}

public sealed record ManualDocumentFilterResult(
    ManualDocumentCategory Category,
    string Extension,
    string Reason);
