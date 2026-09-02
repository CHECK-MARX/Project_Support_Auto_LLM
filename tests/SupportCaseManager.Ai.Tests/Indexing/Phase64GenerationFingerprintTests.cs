using System.Text.Json;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class Phase64GenerationFingerprintTests
{
    [Fact]
    public void ManualGenerationFingerprint_IsDeterministic_AndConfigurationChangesAreVisible()
    {
        var first = GenerationManifest.CreateManual();
        var second = GenerationManifest.CreateManual();

        Assert.Equal(first, second);
        Assert.NotEqual(first.Parser.ConfigurationHash, GenerationFingerprint.Create(
            first.Parser.Id,
            first.Parser.Version,
            "changed-parser-configuration").ConfigurationHash);
        Assert.NotEqual(first.Chunker.ConfigurationHash, GenerationFingerprint.Create(
            first.Chunker.Id,
            first.Chunker.Version,
            "changed-chunker-configuration").ConfigurationHash);
        Assert.NotEqual(first.Normalization.ConfigurationHash, GenerationFingerprint.Create(
            first.Normalization.Id,
            first.Normalization.Version,
            "changed-normalization-configuration").ConfigurationHash);
    }

    [Fact]
    public void ManualAndOfficialGenerationFingerprints_AreDistinct()
    {
        var manual = GenerationManifest.CreateManual();
        var official = GenerationManifest.CreateOfficial();

        Assert.NotEqual(manual.Parser.ImplementationFingerprint, official.Parser.ImplementationFingerprint);
        Assert.NotEqual(manual.Chunker.ImplementationFingerprint, official.Chunker.ImplementationFingerprint);
        Assert.NotEqual(manual.Normalization.ImplementationFingerprint, official.Normalization.ImplementationFingerprint);
    }

    [Fact]
    public async Task ManualBuilder_PersistsManifestWithoutMachineSpecificValues()
    {
        using var temp = new TempDirectory();
        var corpus = Path.Combine(temp.Path, "manuals");
        var index = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(corpus);
        await File.WriteAllTextAsync(Path.Combine(corpus, "guide.txt"), "qacli analyze project.qaf");

        var result = await new AiManualIndexBuilder().BuildAsync(corpus, index);
        var manifestPath = Path.Combine(index, GenerationManifest.FileName);
        Assert.True(File.Exists(manifestPath));
        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<GenerationManifest>(json);

        Assert.Equal(GenerationManifest.CreateManual(), manifest);
        Assert.DoesNotContain(temp.Path, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.IndexFilePath, json, StringComparison.OrdinalIgnoreCase);
    }
}
