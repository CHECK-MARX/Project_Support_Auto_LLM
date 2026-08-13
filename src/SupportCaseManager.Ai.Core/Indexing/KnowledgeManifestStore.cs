using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Indexing;

internal static class KnowledgeManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<KnowledgeManifest?> LoadAsync(
        string productIndexFolder,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(productIndexFolder, KnowledgeManifest.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<KnowledgeManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task SaveAtomicallyAsync(
        string productIndexFolder,
        KnowledgeManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(productIndexFolder);
        var path = Path.Combine(productIndexFolder, KnowledgeManifest.FileName);
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
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

    public static string BuildSourceFingerprint(IEnumerable<string> values)
    {
        var normalized = string.Join("\n", values.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
