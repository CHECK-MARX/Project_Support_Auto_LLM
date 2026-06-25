using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace SupportCaseManager.Ai.Core.Indexing;

internal static class ManualDocumentTextExtractor
{
    private static readonly Regex ScriptOrStyleRegex = new(
        @"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    static ManualDocumentTextExtractor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<ManualDocumentContent> ReadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var extension = ManualDocumentFilter.NormalizeExtension(Path.GetExtension(filePath));
        return extension switch
        {
            ".md" or ".markdown" => new ManualDocumentContent(
                await ReadTextWithFallbackAsync(filePath, cancellationToken),
                "Markdown"),
            ".txt" or ".text" => new ManualDocumentContent(
                await ReadTextWithFallbackAsync(filePath, cancellationToken),
                "Text"),
            ".csv" => new ManualDocumentContent(
                await ReadTextWithFallbackAsync(filePath, cancellationToken),
                "Csv"),
            ".tsv" => new ManualDocumentContent(
                await ReadTextWithFallbackAsync(filePath, cancellationToken),
                "Tsv"),
            ".html" or ".htm" => new ManualDocumentContent(
                ExtractHtmlText(await ReadTextWithFallbackAsync(filePath, cancellationToken)),
                "Html"),
            ".rst" => new ManualDocumentContent(
                await ReadTextWithFallbackAsync(filePath, cancellationToken),
                "ReStructuredText"),
            ".adoc" or ".asciidoc" => new ManualDocumentContent(
                await ReadTextWithFallbackAsync(filePath, cancellationToken),
                "AsciiDoc"),
            ".pdf" => new ManualDocumentContent(ReadPdfText(filePath), "Pdf"),
            ".docx" => new ManualDocumentContent(ReadDocxText(filePath), "Word"),
            ".xlsx" => new ManualDocumentContent(ReadXlsxText(filePath), "Excel"),
            ".pptx" => new ManualDocumentContent(ReadPptxText(filePath), "PowerPoint"),
            _ => new ManualDocumentContent(
                await ReadTextWithFallbackAsync(filePath, cancellationToken),
                "Text"),
        };
    }

    private static async Task<string> ReadTextWithFallbackAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (TryDecode(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), out var utf8))
        {
            return RemoveUtf8Bom(utf8);
        }

        if (TryDecode(bytes, Encoding.GetEncoding(932), out var shiftJis))
        {
            return shiftJis;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool TryDecode(byte[] bytes, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string RemoveUtf8Bom(string text)
    {
        return text.Length > 0 && text[0] == '\uFEFF'
            ? text[1..]
            : text;
    }

    private static string ReadPdfText(string filePath)
    {
        var builder = new StringBuilder();
        using var document = PdfDocument.Open(filePath);
        foreach (var page in document.GetPages())
        {
            if (!string.IsNullOrWhiteSpace(page.Text))
            {
                builder.AppendLine(page.Text);
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string ReadDocxText(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        return ExtractOpenXmlEntries(
            archive,
            static entryName => entryName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                entryName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                entryName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadXlsxText(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        return ExtractOpenXmlEntries(
            archive,
            static entryName => entryName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                entryName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadPptxText(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        return ExtractOpenXmlEntries(
            archive,
            static entryName => entryName.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase) ||
                entryName.StartsWith("ppt/notesSlides/", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractOpenXmlEntries(
        ZipArchive archive,
        Func<string, bool> shouldReadEntry)
    {
        var builder = new StringBuilder();
        foreach (var entry in archive.Entries
            .Where(entry => shouldReadEntry(entry.FullName.Replace('\\', '/')))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            AppendOpenXmlText(document.Root, builder);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendOpenXmlText(XElement? element, StringBuilder builder)
    {
        if (element is null)
        {
            return;
        }

        foreach (var node in element.Nodes())
        {
            if (node is XElement child)
            {
                var localName = child.Name.LocalName;
                if (localName is "t")
                {
                    builder.Append(child.Value);
                    builder.Append(' ');
                    continue;
                }

                if (localName is "tab")
                {
                    builder.Append('\t');
                    continue;
                }

                if (localName is "br" or "p" or "tr")
                {
                    AppendOpenXmlText(child, builder);
                    builder.AppendLine();
                    continue;
                }

                AppendOpenXmlText(child, builder);
            }
        }
    }

    private static string ExtractHtmlText(string html)
    {
        var withoutScripts = ScriptOrStyleRegex.Replace(html, " ");
        var withoutTags = HtmlTagRegex.Replace(withoutScripts, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }
}

internal sealed record ManualDocumentContent(string Text, string DocumentType);
