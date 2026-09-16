using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SupportCaseManager.Ai.Core.Artifacts;

/// <summary>
/// Translates logical Word paragraphs in place on a copied package. The document
/// package, parts, relationships, and run properties remain intact.
/// </summary>
internal sealed class WordDocumentTranslationHandler : IArtifactTranslationHandler
{
    public ArtifactFormat Format => ArtifactFormat.WordDocument;

    public ArtifactTextTranslationPlan Extract(string filePath, CancellationToken cancellationToken = default)
    {
        using var document = WordprocessingDocument.Open(filePath, false);
        var entries = new List<ArtifactTextTranslationEntry>();
        foreach (var target in EnumerateParagraphs(document, cancellationToken))
        {
            var sourceText = target.Paragraph.InnerText;
            var (shouldTranslate, skipReason) = Evaluate(target.Paragraph, sourceText);
            entries.Add(new ArtifactTextTranslationEntry
            {
                Key = target.Key,
                Location = target.Location,
                SourceText = sourceText,
                ShouldTranslate = shouldTranslate,
                SkipReason = skipReason,
            });
        }

        foreach (var unsupported in EnumerateUnsupportedDrawingText(document, cancellationToken))
        {
            entries.Add(unsupported);
        }

        return new ArtifactTextTranslationPlan { Entries = entries };
    }

    public void Apply(
        string filePath,
        ArtifactTextTranslationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        CancellationToken cancellationToken = default)
    {
        var byKey = translations.ToDictionary(static item => item.Key, StringComparer.Ordinal);
        using var document = WordprocessingDocument.Open(filePath, true);
        foreach (var target in EnumerateParagraphs(document, cancellationToken))
        {
            if (!byKey.TryGetValue(target.Key, out var translation))
            {
                continue;
            }

            var textNodes = target.Paragraph.Descendants<Text>().ToArray();
            var currentText = target.Paragraph.InnerText;
            if (!string.Equals(currentText, translation.SourceText, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The Word paragraph changed after plan approval: {target.Location}");
            }

            if (textNodes.Length == 0)
            {
                throw new InvalidDataException($"The Word paragraph has no editable text: {target.Location}");
            }

            // Keep existing run elements, breaks, tabs, and their formatting; the
            // translated logical paragraph is placed in its first editable text node.
            textNodes[0].Text = translation.TranslatedText;
            textNodes[0].Space = SpaceProcessingModeValues.Preserve;
            foreach (var text in textNodes.Skip(1))
            {
                text.Text = string.Empty;
            }
        }
    }

    private static (bool ShouldTranslate, string SkipReason) Evaluate(Paragraph paragraph, string sourceText)
    {
        if (paragraph.Descendants<FieldCode>().Any() || paragraph.Descendants<SimpleField>().Any())
        {
            return (false, "フィールドコードを含む段落");
        }

        if (paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.Contains("code", StringComparison.OrdinalIgnoreCase) == true
            || paragraph.Descendants<RunStyle>().Any(style => style.Val?.Value?.Contains("code", StringComparison.OrdinalIgnoreCase) == true))
        {
            return (false, "コードスタイルの段落");
        }

        return ExcelTextTranslationPolicy.Evaluate(sourceText, isFormula: false);
    }

    private static IEnumerable<WordParagraphTarget> EnumerateParagraphs(
        WordprocessingDocument document,
        CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var root in GetRoots(document))
        {
            foreach (var paragraph in root.Element.Descendants<Paragraph>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new WordParagraphTarget(
                    $"word:{root.Scope}:{index}",
                    $"{root.Scope} 段落 {index + 1}",
                    paragraph);
                index++;
            }
        }
    }

    private static IEnumerable<(string Scope, OpenXmlElement Element)> GetRoots(WordprocessingDocument document)
    {
        if (document.MainDocumentPart?.Document is { } mainDocument)
        {
            yield return ("本文", mainDocument);
        }

        foreach (var part in document.MainDocumentPart?.HeaderParts ?? [])
        {
            if (part.Header is not null)
            {
                yield return ("ヘッダー", part.Header);
            }
        }

        foreach (var part in document.MainDocumentPart?.FooterParts ?? [])
        {
            if (part.Footer is not null)
            {
                yield return ("フッター", part.Footer);
            }
        }

        if (document.MainDocumentPart?.FootnotesPart?.Footnotes is { } footnotes)
        {
            yield return ("脚注", footnotes);
        }

        if (document.MainDocumentPart?.EndnotesPart?.Endnotes is { } endnotes)
        {
            yield return ("文末脚注", endnotes);
        }
    }

    private static IEnumerable<ArtifactTextTranslationEntry> EnumerateUnsupportedDrawingText(
        WordprocessingDocument document,
        CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var root in GetRoots(document))
        {
            foreach (var drawing in root.Element.Descendants<Drawing>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = drawing.InnerText;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                yield return new ArtifactTextTranslationEntry
                {
                    Key = $"word:{root.Scope}:drawing:{index}",
                    Location = $"{root.Scope} Drawing/TextBox {index + 1}",
                    SourceText = text,
                    ShouldTranslate = false,
                    SkipReason = "Drawing/TextBox内テキストは現在未対応",
                };
                index++;
            }
        }
    }

    private sealed record WordParagraphTarget(string Key, string Location, Paragraph Paragraph);
}
