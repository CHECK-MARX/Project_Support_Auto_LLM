using System.Text.Json;

namespace SupportCaseManager.Ai.Tests.Documentation;

public sealed class AiUsageDocumentationTests
{
    [Fact]
    public async Task AiInventory_IsValidJsonAndDocumentsActualAiAssets()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "config", "ai-inventory.json");

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var json = document.RootElement;

        Assert.Equal("SupportCaseManager AI Assistant", json.GetProperty("applicationName").GetString());
        Assert.Equal("Ollama", json.GetProperty("aiProvider").GetProperty("name").GetString());
        Assert.Equal("http://localhost:11434/api/chat", json.GetProperty("endpoint").GetString());
        Assert.False(json.GetProperty("cloudLlmDefault").GetBoolean());
        Assert.True(json.GetProperty("localOnlyDefault").GetBoolean());

        var modelNames = json
            .GetProperty("models")
            .EnumerateArray()
            .Select(model => model.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("qwen3:14b", modelNames);

        var dataSources = json
            .GetProperty("ragDataSources")
            .EnumerateArray()
            .Select(source => source.GetString())
            .ToList();
        Assert.Contains("PastCaseNote", dataSources);
        Assert.Contains("Manual", dataSources);
        Assert.Contains("OfficialDoc", dataSources);
        Assert.Contains("CuratedFactCatalog", dataSources);
    }

    [Theory]
    [InlineData("Ollama")]
    [InlineData("qwen3:14b")]
    [InlineData("RAG")]
    [InlineData("retrieval augmented generation")]
    [InlineData("evidence retrieval")]
    [InlineData("ai-index")]
    [InlineData("CuratedFactCatalog")]
    [InlineData("human review")]
    [InlineData("AI-BOM")]
    [InlineData("AI Supply Chain")]
    public async Task AiUsageDocument_ContainsAiBomReviewKeywords(string keyword)
    {
        var root = FindRepositoryRoot();
        var text = await File.ReadAllTextAsync(Path.Combine(root, "docs", "AI_USAGE.md"));

        Assert.Contains(keyword, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readme_LinksAiInventoryAndUsageDocumentation()
    {
        var root = FindRepositoryRoot();
        var text = await File.ReadAllTextAsync(Path.Combine(root, "README.md"));

        Assert.Contains("config/ai-inventory.json", text);
        Assert.Contains("docs/AI_USAGE.md", text);
        Assert.Contains("qwen3:14b", text);
        Assert.Contains("AI-BOM", text);
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "")
    {
        foreach (var startDirectory in CandidateStartDirectories(testFilePath))
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SupportCaseManager.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not find repository root.");
    }

    private static IEnumerable<string> CandidateStartDirectories(string testFilePath)
    {
        yield return AppContext.BaseDirectory;

        var sourceDirectory = Path.GetDirectoryName(testFilePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            yield return sourceDirectory;
        }
    }
}
