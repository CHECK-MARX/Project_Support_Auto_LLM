using System.Security.Cryptography;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.Diagnostics;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class QuickDiagnosticServiceTests
{
    [Fact]
    public async Task NoCaseSelected_WarnsWithoutCrashing()
    {
        var report = await new QuickDiagnosticService().RunAsync(Empty(), CancellationToken.None);

        Assert.Equal(QuickDiagnosticStatus.Warn, Item(report, "Case Context").Status);
        Assert.Equal(QuickDiagnosticStatus.Pass, Item(report, "GPT Registration").Status);
        Assert.Equal(QuickDiagnosticStatus.Pass, Item(report, "GPT Handoff").Status);
    }

    [Theory]
    [InlineData(GptRegistrationStates.Registered, "https://chatgpt.com/c/6a70b0a5-1018-83ee-b405-11a2a80326f4", QuickDiagnosticStatus.Pass)]
    [InlineData(GptRegistrationStates.Registered, "https://example.com/wrong", QuickDiagnosticStatus.Fail)]
    [InlineData(GptRegistrationStates.Unregistered, "", QuickDiagnosticStatus.Pass)]
    public async Task GptRegistration_UsesCurrentCaseIdentityAndValidatesUrl(
        string state, string url, QuickDiagnosticStatus expected)
    {
        var snapshot = Empty() with
        {
            SupportId = "00018303",
            Product = "Checkmarx",
            Registration = new GptHandoffContext
            {
                SupportId = "00018303", Product = "Checkmarx", RegistrationState = state,
                ConversationUrl = url,
            },
        };

        var report = await new QuickDiagnosticService().RunAsync(snapshot, CancellationToken.None);

        Assert.Equal(expected, Item(report, "GPT Registration").Status);
        Assert.Equal(expected, Item(report, "GPT Conversation URL").Status);
    }

    [Fact]
    public async Task RoutingDryRuns_UseExistingResolverWithoutSideEffects()
    {
        var snapshot = Empty();
        var report = await new QuickDiagnosticService().RunAsync(snapshot, CancellationToken.None);

        foreach (var name in new[] { "Customer Reply Routing", "Manufacturer ASK", "Manufacturer REPLY",
            "Manufacturer Translation", "Normal Chat Isolation" })
            Assert.Equal(QuickDiagnosticStatus.Pass, Item(report, name).Status);
        Assert.Equal(QuickDiagnosticStatus.Warn, Item(report, "Artifact Isolation").Status);
        Assert.Equal(string.Empty, snapshot.CaseFolder);
    }

    [Fact]
    public async Task Run_IsReadOnlyAndRepeatedExecutionIsIdempotent()
    {
        using var folder = new TemporaryDirectory();
        var index = Path.Combine(folder.Path, "cases-index.json");
        var caseFolder = Path.Combine(folder.Path, "case");
        Directory.CreateDirectory(caseFolder);
        await File.WriteAllTextAsync(index, "[]");
        var snapshot = Empty() with
        {
            SupportId = "00018303", Product = "Checkmarx", CaseFolder = caseFolder,
            BaseFolder = folder.Path, CurrentCase = new CaseContext
            {
                SupportNumber = "00018303", ProductName = "Checkmarx", CaseFolderPath = caseFolder,
            },
        };
        var before = Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => SHA256.HashData(File.ReadAllBytes(file)), StringComparer.OrdinalIgnoreCase);

        var first = await new QuickDiagnosticService().RunAsync(snapshot, CancellationToken.None);
        var second = await new QuickDiagnosticService().RunAsync(snapshot, CancellationToken.None);

        Assert.Equal(first.Items, second.Items);
        Assert.Equal(QuickDiagnosticStatus.Pass, Item(first, "Case Folder").Status);
        Assert.Equal(QuickDiagnosticStatus.Pass, Item(first, "OneDrive/File Access").Status);
        Assert.Equal(before.Keys.OrderBy(x => x), Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories).OrderBy(x => x));
        foreach (var (path, hash) in before) Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task CancelledRun_DoesNotReadOrMutateFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuickDiagnosticService().RunAsync(Empty(), cancellation.Token));
    }

    [Fact]
    public async Task ExistingHandoff_UsesStoredHistoryParserWithoutWriting()
    {
        using var folder = new TemporaryDirectory();
        var path = Path.Combine(folder.Path, "GPT連携内容_00018303.txt");
        var sections = GptHandoffFormat.SectionOrder.ToDictionary(section => section, _ => "確認済み");
        var text = "*****追記部_20260916_120000*****\n" +
            GptHandoffParser.Canonicalize(new GptHandoffSnapshot(sections));
        await File.WriteAllTextAsync(path, text);
        var snapshot = Empty() with { SupportId = "00018303", Product = "Checkmarx", CaseFolder = folder.Path };

        var report = await new QuickDiagnosticService().RunAsync(snapshot, CancellationToken.None);

        Assert.Equal(QuickDiagnosticStatus.Pass, Item(report, "GPT Handoff").Status);
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    private static QuickDiagnosticItem Item(QuickDiagnosticReport report, string name) =>
        Assert.Single(report.Items, item => item.Name == name);

    private static QuickDiagnosticSnapshot Empty() => new(
        "", "", "", "", "", "", "", null, new GptHandoffContext(), [], []);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quick-diagnostic-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
