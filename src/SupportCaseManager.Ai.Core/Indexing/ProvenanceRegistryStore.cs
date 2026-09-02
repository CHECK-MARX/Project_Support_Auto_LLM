using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Indexing;

public static class ProvenanceRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task SaveAsync(
        string indexFolder,
        IReadOnlyList<SourceRegistryEntry> entries,
        IReadOnlyList<ParsedSourceArtifact> artifacts,
        CancellationToken cancellationToken = default,
        GenerationManifest? generationManifest = null)
    {
        if (entries.Count == 0 && artifacts.Count == 0 && generationManifest is null)
        {
            return;
        }

        Directory.CreateDirectory(indexFolder);
        var existingRegistry = await LoadAsync<SourceRegistryDocument>(Path.Combine(indexFolder, SourceRegistryDocument.FileName), cancellationToken)
            ?? new SourceRegistryDocument();
        var existingArtifacts = await LoadAsync<ParsedSourceArtifactDocument>(Path.Combine(indexFolder, ParsedSourceArtifactDocument.FileName), cancellationToken)
            ?? new ParsedSourceArtifactDocument();

        var registry = existingRegistry.Sources
            .Concat(entries)
            .GroupBy(static entry => entry.LogicalSourceId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static entry => entry.LogicalSourceId, StringComparer.Ordinal)
            .ToList();
        var parsedArtifacts = existingArtifacts.Sources
            .Concat(artifacts)
            .GroupBy(static artifact => artifact.LogicalSourceId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static artifact => artifact.LogicalSourceId, StringComparer.Ordinal)
            .ToList();

        await SaveAtomicallyAsync(
            Path.Combine(indexFolder, SourceRegistryDocument.FileName),
            new SourceRegistryDocument { Sources = registry },
            cancellationToken);
        await SaveAtomicallyAsync(
            Path.Combine(indexFolder, ParsedSourceArtifactDocument.FileName),
            new ParsedSourceArtifactDocument { Sources = parsedArtifacts },
            cancellationToken);
        if (generationManifest is not null)
        {
            await SaveAtomicallyAsync(
                Path.Combine(indexFolder, GenerationManifest.FileName),
                generationManifest,
                cancellationToken);
        }
    }

    private static async Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static async Task SaveAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
