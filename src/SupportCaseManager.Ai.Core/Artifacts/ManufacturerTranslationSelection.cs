using System.Security.Cryptography;

namespace SupportCaseManager.Ai.Core.Artifacts;

/// <summary>An explicit source/output association, stored with the existing case session.</summary>
public sealed record ManufacturerTranslationSelection
{
    public string SourcePath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public string OutputHash { get; init; } = string.Empty;

    public static ManufacturerTranslationSelection Create(string caseFolder, string source, string output)
    {
        var policy = new CaseArtifactPathPolicy();
        source = policy.NormalizeSelectedSourceFile(caseFolder, source);
        output = policy.NormalizeSelectedSourceFile(caseFolder, output);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase)
            || !CaseArtifactPathPolicy.IsSupportedSource(source)
            || !Path.GetExtension(source).Equals(Path.GetExtension(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("英訳元と別名の同形式英訳ファイルを指定してください。");
        return new() { SourcePath = source, OutputPath = output,
            SourceHash = Hash(source), OutputHash = Hash(output) };
    }

    public bool IsValid(string caseFolder)
    {
        try
        {
            var current = Create(caseFolder, SourcePath, OutputPath);
            return current.SourceHash == SourceHash && current.OutputHash == OutputHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
