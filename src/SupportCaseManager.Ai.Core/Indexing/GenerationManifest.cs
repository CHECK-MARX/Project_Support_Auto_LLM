using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class GenerationManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "provenance-generation-manifest.json";

    [JsonPropertyName("provenanceSchemaVersion")]
    public int ProvenanceSchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("parser")]
    public GenerationFingerprint Parser { get; init; } = new();

    [JsonPropertyName("chunker")]
    public GenerationFingerprint Chunker { get; init; } = new();

    [JsonPropertyName("normalization")]
    public GenerationFingerprint Normalization { get; init; } = new();

    [JsonPropertyName("generatorVersion")]
    public string GeneratorVersion { get; init; } = string.Empty;

    [JsonPropertyName("sourceRegistryVersion")]
    public int SourceRegistryVersion { get; init; } = SourceRegistryDocument.CurrentSchemaVersion;

    [JsonPropertyName("parsedArtifactVersion")]
    public int ParsedArtifactVersion { get; init; } = ParsedSourceArtifactDocument.CurrentSchemaVersion;

    public static GenerationManifest CreateManual() => Create(
        parserId: "ManualDocumentTextExtractor",
        parserVersion: "pdfpig-0.1.14;openxml-3.5.1;utf8-text-1",
        parserConfiguration: "pdf:text-pages;docx:openxml-body;archive:zip-safe-reader",
        chunkerId: "AiManualIndexBuilder",
        chunkerVersion: "1",
        chunkerConfiguration: "max=2600;overlap=150;offset=utf16;markdown-heading=1-3",
        normalizationId: "ManualTextNormalization",
        normalizationVersion: "1",
        normalizationConfiguration: "line-endings=lf;unicode=preserve;whitespace=preserve;page-join=none",
        generatorVersion: "phase64-provenance-substrate-v1");

    public static GenerationManifest CreateOfficial() => Create(
        parserId: "OfficialDocumentHtmlExtractor",
        parserVersion: "html-dom-text-1",
        parserConfiguration: "title-heading-body;ignored-elements=script,style,navigation",
        chunkerId: "AiOfficialDocumentIndexBuilder",
        chunkerVersion: "1",
        chunkerConfiguration: "max=2600;overlap=150;offset=utf16",
        normalizationId: "OfficialHtmlTextNormalization",
        normalizationVersion: "1",
        normalizationConfiguration: "line-endings=lf;unicode=preserve;whitespace=collapse-html",
        generatorVersion: "phase64-provenance-substrate-v1");

    private static GenerationManifest Create(
        string parserId,
        string parserVersion,
        string parserConfiguration,
        string chunkerId,
        string chunkerVersion,
        string chunkerConfiguration,
        string normalizationId,
        string normalizationVersion,
        string normalizationConfiguration,
        string generatorVersion) => new()
    {
        Parser = GenerationFingerprint.Create(parserId, parserVersion, parserConfiguration),
        Chunker = GenerationFingerprint.Create(chunkerId, chunkerVersion, chunkerConfiguration),
        Normalization = GenerationFingerprint.Create(normalizationId, normalizationVersion, normalizationConfiguration),
        GeneratorVersion = generatorVersion,
    };
}

public sealed record class GenerationFingerprint
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("configurationHash")]
    public string ConfigurationHash { get; init; } = string.Empty;

    [JsonPropertyName("implementationFingerprint")]
    public string ImplementationFingerprint { get; init; } = string.Empty;

    public static GenerationFingerprint Create(string id, string version, string configuration)
    {
        var canonical = string.Join("\n", id.Trim(), version.Trim(), configuration.Trim());
        return new GenerationFingerprint
        {
            Id = id,
            Version = version,
            ConfigurationHash = Hash(configuration),
            ImplementationFingerprint = Hash(canonical),
        };
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
