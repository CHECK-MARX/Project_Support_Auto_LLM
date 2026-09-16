using SupportCaseManager.Core.Cases;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Cases;

public sealed class GptHandoffImportServiceTests
{
    private static readonly DateTimeOffset ImportTime =
        new(2026, 9, 16, 21, 30, 0, TimeSpan.FromHours(9));

    [Fact]
    public void ClipboardParser_RequiresMarkersAndAllSevenSections()
    {
        Assert.False(GptHandoffParser.TryParseClipboard(string.Empty, out _, out _));
        Assert.False(GptHandoffParser.TryParseClipboard("【メーカー担当者】\nChen", out _, out _));
        Assert.True(GptHandoffParser.TryParseClipboard(ClipboardText(Snapshot()), out var parsed, out _));
        Assert.Equal("Chen Chen", parsed[GptHandoffSection.ManufacturerContact]);
    }

    [Fact]
    public void ClipboardParser_AllowsAnEmptyOptionalSectionWhenHeaderIsPresent()
    {
        var snapshot = Snapshot(new Dictionary<GptHandoffSection, string>
        {
            [GptHandoffSection.LatestManufacturerResponseSummary] = string.Empty,
        });

        Assert.True(GptHandoffParser.TryParseClipboard(ClipboardText(snapshot), out var parsed, out _));
        Assert.Empty(parsed[GptHandoffSection.LatestManufacturerResponseSummary]);
    }

    [Fact]
    public void ClipboardParser_AcceptsChatGptMarkdownAroundContractStructure()
    {
        var canonical = GptHandoffParser.Canonicalize(Snapshot());
        foreach (var section in GptHandoffFormat.SectionOrder)
        {
            var header = $"【{GptHandoffFormat.Label(section)}】";
            canonical = canonical.Replace(header, $"**{header}**", StringComparison.Ordinal);
        }

        var clipboard = $"""
            ```text
            **{GptHandoffFormat.StartMarker}**
            {canonical}
            **{GptHandoffFormat.EndMarker}**
            ```
            """;

        Assert.True(GptHandoffParser.TryParseClipboard(clipboard, out var parsed, out var error), error);
        Assert.Equal("Chen Chen", parsed[GptHandoffSection.ManufacturerContact]);
    }

    [Fact]
    public void ClipboardParser_UsesValidBlockWhenClipboardContainsAnEarlierQuotedContract()
    {
        var clipboard = $"""
            {GptHandoffFormat.StartMarker}
            省略
            {GptHandoffFormat.EndMarker}

            {ClipboardText(Snapshot())}
            """;

        Assert.True(GptHandoffParser.TryParseClipboard(clipboard, out var parsed, out var error), error);
        Assert.Equal("顧客同意確認", parsed[GptHandoffSection.UnresolvedItems]);
    }

    [Fact]
    public async Task FirstImportCreatesSingleAppendOnlyCaseFile()
    {
        using var temp = new TempDirectory();
        var result = await Service().ImportAsync(Case(temp.Path), Snapshot());

        Assert.Equal(GptHandoffImportStatus.Imported, result.Status);
        Assert.Equal(1, result.ImportVersion);
        Assert.Equal(Path.Combine(temp.Path, "GPT連携内容_00018303.txt"), result.FilePath);
        var text = await File.ReadAllTextAsync(result.FilePath);
        Assert.Contains(
            $"*****追記部_{ImportTime:yyyy/MM/dd HH:mm:ss}(GPT取込)******",
            text,
            StringComparison.Ordinal);
        Assert.Contains("【現在の未解決事項】", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameContentTwiceDoesNotAppendOrIncreaseVersion()
    {
        using var temp = new TempDirectory();
        var service = Service();
        var caseRecord = Case(temp.Path);
        var first = await service.ImportAsync(caseRecord, Snapshot());
        var before = await File.ReadAllTextAsync(first.FilePath);

        var second = await service.ImportAsync(caseRecord, Snapshot());

        Assert.Equal(GptHandoffImportStatus.NoChange, second.Status);
        Assert.Equal(1, second.ImportVersion);
        Assert.Equal(before, await File.ReadAllTextAsync(first.FilePath));
    }

    [Fact]
    public async Task ConcurrentSameImportWritesOnlyOneEntry()
    {
        using var temp = new TempDirectory();
        var service = Service();
        var caseRecord = Case(temp.Path);

        var results = await Task.WhenAll(
            service.ImportAsync(caseRecord, Snapshot()),
            service.ImportAsync(caseRecord, Snapshot()));

        Assert.Single(results, static result => result.Status == GptHandoffImportStatus.Imported);
        Assert.Single(results, static result => result.Status == GptHandoffImportStatus.NoChange);
        var text = await File.ReadAllTextAsync(Path.Combine(temp.Path, "GPT連携内容_00018303.txt"));
        Assert.Equal(1, text.Split("*****追記部_", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task ChangedImportAppendsOnlyChangedSectionsAndPreservesResolutionHistory()
    {
        using var temp = new TempDirectory();
        var service = Service();
        var caseRecord = Case(temp.Path);
        await service.ImportAsync(caseRecord, Snapshot());
        var updated = Snapshot(new Dictionary<GptHandoffSection, string>
        {
            [GptHandoffSection.UnresolvedItems] = "なし",
            [GptHandoffSection.ResolvedItems] = "顧客同意確認は完了",
        });

        var result = await service.ImportAsync(caseRecord, updated);

        Assert.Equal(GptHandoffImportStatus.Imported, result.Status);
        Assert.Equal(
            [GptHandoffSection.UnresolvedItems, GptHandoffSection.ResolvedItems],
            result.ChangedSections.OrderBy(static value => value));
        var text = await File.ReadAllTextAsync(result.FilePath);
        Assert.Contains("顧客同意確認", text, StringComparison.Ordinal);
        Assert.Contains("顧客同意確認は完了", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("【メーカー担当者】", StringSplitOptions.None).Length - 1);
        Assert.True(GptHandoffParser.TryBuildEffectiveSnapshot(text, out var effective, out var count));
        Assert.Equal(2, count);
        Assert.Equal("なし", effective[GptHandoffSection.UnresolvedItems]);
    }

    [Fact]
    public async Task RegistrationAndSupportIdGuardsPreventCrossCaseSave()
    {
        using var temp = new TempDirectory();
        var unregistered = Case(temp.Path);
        unregistered.GptRegistration = new GptCaseRegistration();
        var mismatched = Case(temp.Path);
        mismatched.GptRegistration.SupportId = "00019999";

        var first = await Service().ImportAsync(unregistered, Snapshot());
        var second = await Service().ImportAsync(mismatched, Snapshot());

        Assert.Equal(GptHandoffImportStatus.Blocked, first.Status);
        Assert.Equal(GptHandoffImportStatus.Blocked, second.Status);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    private static GptHandoffImportService Service() => new(() => ImportTime);

    private static CaseRecord Case(string folder)
    {
        var record = new CaseRecord(
            "ABC", "00018303", "調査中", "20260916", Path.GetFileName(folder), folder, "2026-09-16T00:00:00Z");
        record.GptRegistration = new GptCaseRegistration
        {
            SupportId = "00018303",
            Product = "Checkmarx",
            TargetGptKey = "checkmarx",
            ConversationUrl = "https://chatgpt.com/c/existing",
            RegistrationState = GptRegistrationStates.Registered,
        };
        return record;
    }

    private static GptHandoffSnapshot Snapshot(IReadOnlyDictionary<GptHandoffSection, string>? overrides = null)
    {
        var values = new Dictionary<GptHandoffSection, string>
        {
            [GptHandoffSection.ManufacturerContact] = "Chen Chen",
            [GptHandoffSection.NewlyEstablishedFacts] = "Version 9.7.4で確認済み",
            [GptHandoffSection.UnresolvedItems] = "顧客同意確認",
            [GptHandoffSection.ResolvedItems] = "Service User作成条件",
            [GptHandoffSection.LatestManufacturerResponseSummary] = "設定条件の回答あり",
            [GptHandoffSection.CustomerHandlingNotes] = "正式回答前にメーカー原文を確認",
            [GptHandoffSection.NextAction] = "顧客へ同意を確認",
        };
        foreach (var item in overrides ?? new Dictionary<GptHandoffSection, string>())
        {
            values[item.Key] = item.Value;
        }
        return new GptHandoffSnapshot(values);
    }

    private static string ClipboardText(GptHandoffSnapshot snapshot) =>
        $"{GptHandoffFormat.StartMarker}\n{GptHandoffParser.Canonicalize(snapshot)}\n{GptHandoffFormat.EndMarker}";
}
