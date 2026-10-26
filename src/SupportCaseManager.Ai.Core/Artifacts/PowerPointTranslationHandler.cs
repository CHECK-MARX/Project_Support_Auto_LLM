using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace SupportCaseManager.Ai.Core.Artifacts;

/// <summary>Edits text in a copied presentation without rebuilding its package or drawing objects.</summary>
internal sealed class PowerPointTranslationHandler : IArtifactTranslationHandler
{
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace StrictDrawing = "http://purl.oclc.org/ooxml/drawingml/main";
    private static readonly XNamespace Chart = "http://schemas.openxmlformats.org/drawingml/2006/chart";

    public ArtifactFormat Format => ArtifactFormat.PowerPointPresentation;

    public ArtifactTextTranslationPlan Extract(string filePath, CancellationToken cancellationToken = default)
    {
        ValidatePackage(filePath);
        using var archive = ZipFile.OpenRead(filePath);
        var entries = new List<ArtifactTextTranslationEntry>();
        foreach (var part in TextParts(archive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = ReadXml(part);
            foreach (var target in Targets(part.FullName, xml))
            {
                var source = string.Concat(target.Nodes.Select(node => node.Value));
                var policy = target.IsField
                    ? (ShouldTranslate: false, Reason: "自動更新フィールド（スライド番号・日付等）")
                    : ExcelTextTranslationPolicy.Evaluate(source, isFormula: false);
                entries.Add(new ArtifactTextTranslationEntry
                {
                    Key = target.Key,
                    Location = target.Location,
                    SourceText = source,
                    ShouldTranslate = policy.ShouldTranslate,
                    SkipReason = policy.Reason,
                });
            }

            // Category caches must stay consistent with their embedded/linked workbook.
            // Do not silently change only the display cache of a linked chart.
            foreach (var cache in xml.Descendants(Chart + "strCache").SelectMany(node => node.Descendants(Chart + "v")))
            {
                if (!ExcelTextTranslationPolicy.Evaluate(cache.Value, false).ShouldTranslate)
                    continue;
                entries.Add(new ArtifactTextTranslationEntry
                {
                    Key = $"pptx:{part.FullName}:chart-cache:{entries.Count}",
                    Location = $"{part.FullName} グラフ参照データ",
                    SourceText = cache.Value,
                    ShouldTranslate = false,
                    SkipReason = "埋込／リンク元Excelと連動するグラフ参照データは変更しません",
                });
            }
        }

        var warnings = new List<string>();
        if (archive.Entries.Any(entry => entry.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)
                                       && entry.Length > 0))
            warnings.Add("画像・動画内の文字は翻訳対象外です。画像・動画自体は保持します。");
        if (archive.Entries.Any(entry => entry.FullName.StartsWith("ppt/embeddings/", StringComparison.Ordinal)
                                       && entry.Length > 0))
            warnings.Add("埋込ファイル・リンク元ファイルは変更しません。グラフの参照データは対象外です。");
        return new ArtifactTextTranslationPlan { Entries = entries, Warnings = warnings };
    }

    public void Apply(string filePath, ArtifactTextTranslationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations, CancellationToken cancellationToken = default)
    {
        var byKey = translations.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Update);
        foreach (var part in TextParts(archive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = ReadXml(part);
            var changed = false;
            foreach (var target in Targets(part.FullName, xml))
            {
                if (!byKey.TryGetValue(target.Key, out var translation))
                    continue;
                if (target.IsField || !string.Equals(string.Concat(target.Nodes.Select(node => node.Value)),
                        translation.SourceText, StringComparison.Ordinal))
                    throw new InvalidDataException($"PowerPointの原文が計画と一致しません: {target.Location}");

                // Each target has uniform run properties, one hyperlink scope and no line breaks.
                // Preserve every run/property/object; replace only its text content.
                target.Nodes[0].Value = translation.TranslatedText;
                foreach (var node in target.Nodes.Skip(1))
                    node.Value = string.Empty;
                applied.Add(target.Key);
                changed = true;
            }
            if (!changed)
                continue;
            using var stream = part.Open();
            stream.SetLength(0);
            xml.Save(stream, SaveOptions.DisableFormatting);
        }
        if (applied.Count != byKey.Count)
            throw new InvalidDataException("PowerPointの翻訳対象をすべて照合できませんでした。ファイルは保存しません。");
    }

    private static void ValidatePackage(string path)
    {
        try
        {
            using var document = PresentationDocument.Open(path, false);
            if (document.PresentationPart?.Presentation is null)
                throw new InvalidDataException("PowerPointプレゼンテーションの本文がありません。");
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.Ordinal)))
                throw new InvalidDataException("署名付きPPTXは署名を壊さず翻訳できません。署名なしのコピーを指定してください。");
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException)
        {
            throw new InvalidDataException("PPTXを読み取れません。暗号化・破損・形式の不一致を確認してください。", ex);
        }
    }

    private static ZipArchiveEntry[] TextParts(ZipArchive archive) => archive.Entries
        .Where(entry => entry.FullName.StartsWith("ppt/", StringComparison.Ordinal)
                        && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
        .OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();

    private static XDocument ReadXml(ZipArchiveEntry part)
    {
        using var stream = part.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static IEnumerable<TextTarget> Targets(string part, XDocument xml)
    {
        var paragraphIndex = 0;
        foreach (var paragraph in xml.Descendants().Where(node => IsDrawing(node, "p")))
        {
            var targetIndex = 0;
            var nodes = new List<XElement>();
            string? properties = null;
            foreach (var child in paragraph.Elements())
            {
                var texts = child.Elements().Where(node => IsDrawing(node, "t")).ToArray();
                var isRun = IsDrawing(child, "r") && texts.Length > 0;
                var nextProperties = isRun
                    ? child.Elements().FirstOrDefault(node => IsDrawing(node, "rPr"))?.ToString(SaveOptions.DisableFormatting) ?? ""
                    : null;
                if (nodes.Count > 0 && (!isRun || !string.Equals(properties, nextProperties, StringComparison.Ordinal)))
                {
                    yield return Target(part, paragraphIndex, targetIndex++, nodes.ToArray(), false);
                    nodes.Clear();
                }
                if (isRun)
                {
                    properties = nextProperties;
                    nodes.AddRange(texts);
                }
                else if (IsDrawing(child, "fld") && texts.Length > 0)
                    yield return Target(part, paragraphIndex, targetIndex++, texts, true);
            }
            if (nodes.Count > 0)
                yield return Target(part, paragraphIndex, targetIndex, nodes.ToArray(), false);
            paragraphIndex++;
        }
    }

    private static bool IsDrawing(XElement node, string name) => node.Name.LocalName == name
        && (node.Name.Namespace == Drawing || node.Name.Namespace == StrictDrawing);

    private static TextTarget Target(string part, int paragraph, int segment, XElement[] nodes, bool field) => new(
        $"pptx:{part}:p{paragraph}:s{segment}",
        $"{part} 段落 {paragraph + 1} テキスト {segment + 1}", nodes, field);

    private sealed record TextTarget(string Key, string Location, XElement[] Nodes, bool IsField);
}
