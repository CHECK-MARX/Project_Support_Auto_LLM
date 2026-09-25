using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;
using SupportCaseManager.Core.Quality;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class AnswerContextCapsuleComposerTests
{
    [Fact]
    public void KlocworkSyntheticPolish_PreservesConflictingPrimaryEvidenceAndExistingReadiness()
    {
        var snapshot = KlocworkSnapshot();
        const string manufacturer = "Amazon Linux 2023 is known to work, but managed testing for official support begins with Klocwork 2026.4.";

        var capsule = AnswerContextCapsuleComposer.ComposePolish(snapshot, manufacturer);
        var prompt = GptPolishPrompt.Build(QualityAudience.Customer,
            "現時点で正式サポート可否は確認できません。", capsule.Text);

        Assert.Contains("POLISH_CONTEXT_V1", prompt);
        Assert.Contains("Klocwork 2026.3でAmazon Linux 2023は正式サポート対象ですか", prompt);
        Assert.Contains("known to work", capsule.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("managed testing for official support begins with Klocwork 2026.4", prompt);
        Assert.Contains("Added support for Amazon Linux 2023", prompt);
        Assert.Contains("NeedsManufacturerConfirmation", prompt);
        Assert.Contains("2026.3の正式サポート可否は未解決", prompt);
        Assert.Contains("メーカーへ正式サポート可否を確認", prompt);
        Assert.Contains("正式サポートの定義が一致しない", prompt);
        Assert.Contains("完成したお客様向けメールを出さず", prompt);
        Assert.Contains("Current draft", prompt);
        Assert.DoesNotContain("QUALITY_MEMORY_TECHNICAL_FACT", prompt);
        Assert.True(capsule.HasEvidenceConflict);
        Assert.Equal(1, capsule.ManufacturerEvidenceCount);
        Assert.Equal(1, capsule.OfficialEvidenceCount);
    }

    [Fact]
    public void Continuation_UsesLatestInquiryAndUpdatedHandoffWithoutCrossCaseNotes()
    {
        var snapshot = KlocworkSnapshot() with
        {
            InquiryText = "*****追記部_2026/09/01 10:00:00(受付)******\n古い問い合わせ\n" +
                "*****追記部_2026/09/25 10:00:00(追加質問)******\n最新の正式サポート確認\n",
            Notes =
            [
                HandoffNote("00018729", "現在の未解決事項: 古い内容"),
                HandoffNote("00019999", "別案件の秘密事項"),
            ],
        };

        var capsule = AnswerContextCapsuleComposer.ComposeTurn(snapshot,
            "The latest manufacturer response says known to work, with managed testing starting in 2026.4.",
            ["new-evidence.pdf"]);

        Assert.Contains("TURN_CONTEXT_CAPSULE_V1", capsule.Text);
        Assert.Contains("最新の正式サポート確認", capsule.Text);
        Assert.DoesNotContain("古い問い合わせ", capsule.Text);
        Assert.Contains("2026.4", capsule.Text);
        Assert.Contains("new-evidence.pdf", capsule.Text);
        Assert.DoesNotContain("別案件の秘密事項", capsule.Text);
        Assert.Contains("GptHandoff", capsule.SourceTypes);
    }

    [Fact]
    public void ExplicitManufacturerHistoryIsReadButOutboundHistoryIsNotPromoted()
    {
        var snapshot = KlocworkSnapshot() with
        {
            Notes =
            [
                new NoteSnapshot
                {
                    FileName = "メーカー連携内容_00018729.txt",
                    Text = "*****追記部_2026/09/01 10:00:00(メーカへ確認中)******\n" +
                        "Hello Support Team, please check.\n" +
                        "*****追記部_2026/09/25 10:00:00(メーカー回答受信)******\n" +
                        "We know it works, but managed testing starts in 2026.4.\n",
                },
            ],
        };

        var capsule = AnswerContextCapsuleComposer.ComposePolish(snapshot, null);

        Assert.Contains("We know it works", capsule.Text);
        Assert.DoesNotContain("Hello Support Team", capsule.Text);
        Assert.Equal(1, capsule.ManufacturerEvidenceCount);
    }

    [Fact]
    public void SameProductEvidenceAndHandoffFromAnotherCaseAreNotIncluded()
    {
        var snapshot = KlocworkSnapshot() with
        {
            Notes = [HandoffNote("000187290", "別案件の未解決事項")],
            EvidenceConflicts = [],
            Evidence =
            [
                new SearchSource
                {
                    SourceType = "OfficialDoc", ProductName = "Klocwork",
                    SupportNumber = "00019999", Title = "Another case evidence",
                    Text = "ANOTHER_CASE_PRIVATE_EVIDENCE", MatchKind = "Conflicting",
                },
                new SearchSource
                {
                    SourceType = "OfficialDoc", ProductName = "Klocwork",
                    SupportNumber = "00018729", Title = "Current case evidence",
                    Text = "CURRENT_CASE_EVIDENCE",
                },
            ],
        };

        var capsule = AnswerContextCapsuleComposer.ComposeTurn(snapshot, null, []);

        Assert.Contains("CURRENT_CASE_EVIDENCE", capsule.Text);
        Assert.DoesNotContain("ANOTHER_CASE_PRIVATE_EVIDENCE", capsule.Text);
        Assert.DoesNotContain("別案件の未解決事項", capsule.Text);
        Assert.DoesNotContain("Another case evidence", capsule.Text);
        Assert.Equal(1, capsule.OfficialEvidenceCount);
        Assert.False(capsule.HasEvidenceConflict);
    }

    private static CodexCaseSnapshot KlocworkSnapshot() => new()
    {
        ProductName = "Klocwork",
        SupportId = "00018729",
        Status = "メーカー確認中",
        InquiryText = "Klocwork 2026.3でAmazon Linux 2023は正式サポート対象ですか。",
        Readiness = "NeedsManufacturerConfirmation",
        EvidenceConflicts = ["メーカー回答と2026.3公式資料で正式サポートの定義が一致しない"],
        Notes = [HandoffNote("00018729", "2026.3の正式サポート可否は未解決")],
        Evidence =
        [
            new SearchSource
            {
                SourceType = "OfficialDoc", ProductName = "Klocwork", Title = "Klocwork 2026.3 What's New",
                Text = "Added support for Amazon Linux 2023", Score = 0.9,
            },
            new SearchSource
            {
                SourceType = "QualityMemory", ProductName = "Klocwork", Title = "style only",
                Text = "QUALITY_MEMORY_TECHNICAL_FACT", Score = 1,
            },
        ],
    };

    private static NoteSnapshot HandoffNote(string supportId, string unresolved) => new()
    {
        FileName = $"GPT連携内容_{supportId}.txt",
        Text = $"*****追記部_2026/09/25 10:00:00(GPT取込)******\n" +
            $"【現在の未解決事項】\n{unresolved}\n" +
            "【解決済みに変更した事項】\n旧バージョンでの動作確認\n" +
            "【現在の次アクション】\nメーカーへ正式サポート可否を確認\n",
    };
}
