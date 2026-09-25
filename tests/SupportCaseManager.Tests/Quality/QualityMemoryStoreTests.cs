using System.Text.Json;
using SupportCaseManager.Core.Quality;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Tests.Quality;

public sealed class QualityMemoryStoreTests
{
    [Fact]
    public async Task ExistingNoteAndDraftAreNotAutomaticallyApproved()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveDraftAsync("00018303", "Checkmarx", QualityAudience.Customer,
            "CUSTOMER_REPLY", "AI初稿です。");
        Assert.Empty(fixture.Store.Load().Records);
        Assert.Empty(fixture.Store.Retrieve("Checkmarx", QualityAudience.Customer, "CUSTOMER_REPLY"));
    }

    [Fact]
    public async Task ExplicitApprovalIsPersistentAndDuplicateSafe()
    {
        using var fixture = new StoreFixture();
        var request = Approval("株式会社例示様へご案内いたします。support@example.com 00018303 https://example.com/案内", QualityAudience.Customer);
        var first = await fixture.Store.ApproveAsync(request);
        var duplicate = await fixture.Store.ApproveAsync(request);
        var reloaded = new QualityMemoryStore(fixture.FilePath);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(reloaded.Load().Records);
        var retrieved = Assert.Single(reloaded.Retrieve("Checkmarx", QualityAudience.Customer, "CUSTOMER_REPLY"));
        Assert.DoesNotContain("株式会社例示", retrieved.ReusableStyleText);
        Assert.DoesNotContain("00018303", retrieved.ReusableStyleText);
        Assert.DoesNotContain("support@example.com", retrieved.ReusableStyleText);
        Assert.DoesNotContain("https://", retrieved.ReusableStyleText);
    }

    [Fact]
    public async Task ManufacturerRequiresExplicitOutboundDirectionAndIsAudienceSeparated()
    {
        using var fixture = new StoreFixture();
        var inbound = Approval("Hello Support Team,", QualityAudience.Manufacturer) with
        {
            Direction = QualityDirection.ManufacturerInbound,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ApproveAsync(inbound));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ApproveAsync(
            inbound with { Direction = QualityDirection.Unknown }));
        await fixture.Store.ApproveAsync(inbound with { Direction = QualityDirection.ManufacturerOutbound });
        Assert.Empty(fixture.Store.Retrieve("Checkmarx", QualityAudience.Customer, "CUSTOMER_REPLY"));
        Assert.Single(fixture.Store.Retrieve("Checkmarx", QualityAudience.Manufacturer, "MANUFACTURER_ASK"));
    }

    [Fact]
    public async Task RetrievesAtMostThreeApprovedStylesAndHonorsSupersession()
    {
        using var fixture = new StoreFixture();
        var first = await fixture.Store.ApproveAsync(Approval("最初の回答案です。", QualityAudience.Customer));
        await fixture.Store.ApproveAsync(Approval("修正版の回答案です。", QualityAudience.Customer) with
        {
            SupersedesId = first.Id,
        });
        for (var i = 0; i < 5; i++)
            await fixture.Store.ApproveAsync(Approval($"別の回答案{i}です。", QualityAudience.Customer));
        var retrieved = fixture.Store.Retrieve("Checkmarx", QualityAudience.Customer, "CUSTOMER_REPLY");
        Assert.Equal(3, retrieved.Count);
        Assert.DoesNotContain(retrieved, record => record.Id == first.Id);
    }

    [Fact]
    public async Task DraftFinalPairsOnlyWhenUnambiguousAndAfterDraft()
    {
        using var fixture = new StoreFixture();
        var draft = await fixture.Store.SaveDraftAsync("00018303", "Checkmarx", QualityAudience.Customer,
            "CUSTOMER_REPLY", "確認事項はありますか？ 長い説明の初稿です。");
        var approved = await fixture.Store.ApproveAsync(Approval("確認事項を整理しました。", QualityAudience.Customer) with
        {
            Origin = "GPT_REVISED",
            SourceBlockTimestamp = DateTimeOffset.Now.AddSeconds(1),
        });
        Assert.Equal(draft.DraftId, approved.OriginalDraftId);
        Assert.Contains("QUESTION_REMOVAL", approved.CorrectionTags);

        var unrelated = await fixture.Store.ApproveAsync(Approval("別の文章です。", QualityAudience.Customer) with
        {
            Origin = "AI_DRAFT_EDITED",
            SourceBlockTimestamp = DateTimeOffset.Now.AddSeconds(1),
        });
        Assert.Null(unrelated.OriginalDraftId);
    }

    [Fact]
    public async Task UnsafeStoredStyleIsNotRetrieved()
    {
        using var fixture = new StoreFixture();
        var record = await fixture.Store.ApproveAsync(Approval("正式な回答です。", QualityAudience.Customer));
        var document = fixture.Store.Load() with
        {
            Records = [record with { ReusableStyleText = "support@example.com の案件をコピー" }],
        };
        await File.WriteAllTextAsync(fixture.FilePath, JsonSerializer.Serialize(document));
        Assert.Empty(fixture.Store.Retrieve("Checkmarx", QualityAudience.Customer, "CUSTOMER_REPLY"));
    }

    [Fact]
    public async Task MultipleSavedNoteVersionsCanApproveLatestWithoutChangingSource()
    {
        using var fixture = new StoreFixture();
        var source = "*****追記部_2026/09/16 12:30:00(調査中)******\n古い案です。\n" +
            "*****追記部_2026/09/17 09:00:00(調査中)******\n新しい案です。\n";
        var entries = CaseNoteHistoryParser.Parse(source);
        var latest = Assert.IsType<CaseHistoryEntry>(CaseNoteHistoryParser.PickLatest(entries));
        await fixture.Store.ApproveAsync(Approval(latest.Body, QualityAudience.Customer) with
        {
            SourceBlockTimestamp = new DateTimeOffset(latest.Timestamp!.Value),
        });

        Assert.Equal(2, entries.Count);
        Assert.Equal("新しい案です。", latest.Body);
        Assert.Equal(latest.Body, Assert.Single(fixture.Store.Load().Records).ApprovedText);
        Assert.Contains("古い案です。", source);
    }

    [Fact]
    public void GptPolishPromptKeepsAudienceAndOriginalDraftWithoutScraping()
    {
        var customer = GptPolishPrompt.Build(QualityAudience.Customer, "お客様への初稿");
        var manufacturer = GptPolishPrompt.Build(QualityAudience.Manufacturer, "Hello Jim,");
        Assert.Contains("お客様向け", customer);
        Assert.Contains("メーカー向け英語", manufacturer);
        Assert.Contains("Hello Jim,", manufacturer);
        Assert.DoesNotContain("新規Conversation", manufacturer);
    }

    [Fact]
    public void TechnicalValueChangeIsFlaggedButNotConvertedToStyleFact()
    {
        var draft = "Version 9.7.4 を確認してください。";
        var final = "Version 9.7.5 を確認してください。";
        Assert.Contains("TECHNICAL_VALUE_CHANGE_REVIEW_REQUIRED",
            QualityCorrectionAnalyzer.Analyze(draft, final));
        Assert.DoesNotContain("9.7.5", QualityStyleSummary.Build(final, QualityAudience.Customer));
    }

    [Fact]
    public void QualityReviewerReportsStructureWithoutChangingDraft()
    {
        const string draft = "確認してください？\nこの案件を終了します。";
        var review = QualityStyleReviewer.Review(draft, "質問しないでください", 2);
        Assert.Contains("疑問文", review);
        Assert.Contains("案件終了", review);
    }

    private static QualityApprovalRequest Approval(string text, string audience) => new(
        "Checkmarx", audience,
        audience == QualityAudience.Customer ? "CUSTOMER_REPLY" : "MANUFACTURER_ASK",
        audience == QualityAudience.Customer ? QualityDirection.CustomerOutbound : QualityDirection.ManufacturerOutbound,
        "00018303", "保存済みノート.txt", DateTimeOffset.Now, QualityMemoryStore.Hash(text), text);

    private sealed class StoreFixture : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), $"quality-memory-{Guid.NewGuid():N}");
        public string FilePath => Path.Combine(folder, "memory.json");
        public QualityMemoryStore Store { get; }
        public StoreFixture()
        {
            Directory.CreateDirectory(folder);
            Store = new QualityMemoryStore(FilePath);
        }
        public void Dispose() => Directory.Delete(folder, recursive: true);
    }
}
