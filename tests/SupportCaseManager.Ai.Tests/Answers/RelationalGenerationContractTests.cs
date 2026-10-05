using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class RelationalGenerationContractTests
{
    [Fact]
    public void MigrationContract_UsesQuestionRelationshipsAndVerifiedSpanOnly()
    {
        const string inquiry = "社内のクラウドからAzureのVMへの移設を検討しています。";
        const string span = "Azure Managed Instance DBaaS is supported from Checkmarx SAST 9.2.";
        var source = new SearchSource { SourceId = "official:synthetic", SourceType = "OfficialDoc",
            Title = "Components", Text = "Unrelated browser table. " + span + " Unrelated licensing paragraph." };
        var request = Request(inquiry, [Official(source.SourceId, span)]);
        var first = GroundedAnswerPromptBuilder.Build(request, [source]);
        var repair = GroundedAnswerPromptBuilder.Build(request, [source],
            "検証失敗: 移設元と移設先が欠落\n再生成必須Fact: duplicate content");

        Assert.Contains("customerReplyDraftの冒頭", first.UserPrompt);
        Assert.Contains("クラウドからAzureのVMへの移設を検討", first.UserPrompt);
        Assert.Contains("fullTextSHA256=", repair.UserPrompt);
        Assert.Contains($"length={span.Length}", repair.UserPrompt);
        Assert.Contains(span, repair.UserPrompt);
        Assert.DoesNotContain("Unrelated browser", first.UserPrompt);
        Assert.DoesNotContain("duplicate content", repair.UserPrompt);
        Assert.Equal(1, repair.UserPrompt.Split("operation=移設;").Length - 1);
        Assert.Contains("検証失敗: 移設元と移設先が欠落", repair.UserPrompt);
        Assert.DoesNotContain("前回本文（検証不合格、根拠として扱わない）", repair.UserPrompt);
        Assert.Equal(first.OutputSchema!.Value.GetRawText(), repair.OutputSchema!.Value.GetRawText());
        var pattern = repair.OutputSchema.Value.GetProperty("properties").GetProperty("customerReplyDraft").GetProperty("pattern").GetString()!;
        Assert.Matches(pattern, "確認できる事実: お客様は「社内のクラウドからAzureのVMへの移設を検討」という計画を提示されています。" +
            $"選択した公式資料には「{span}」と記載されています。現時点で断定できない事項: 個別環境への適用可否と移設・移行の留意点は、提示根拠だけでは断定できません。追加で必要な確認: 対象環境の構成をメーカーへ確認してください。");
        Assert.DoesNotMatch(pattern, "確認できる事実: DBaaSがサポートされています。");
        Assert.Equal(first.Diagnostics.FinalPromptChars + repair.UserPrompt.Length - first.UserPrompt.Length,
            repair.Diagnostics.FinalPromptChars);
    }

    [Fact]
    public void MigrationChoices_RemainQuestionsInRequiredOpening()
    {
        var request = Request("既存クラウドからAzureのVMへの移設を検討しています。以下の選択肢の可否を教えてください。\n1. Azure SQL Database\n2. Azure SQL Managed Instance\n3. SQL Server on Windows VM", []);
        var prompt = GroundedAnswerPromptBuilder.Build(request, []);
        var pattern = prompt.OutputSchema!.Value.GetProperty("properties").GetProperty("customerReplyDraft").GetProperty("pattern").GetString()!;
        Assert.Contains("お問い合わせの選択肢はAzure SQL Database、Azure SQL Managed Instance、SQL Server on Windows VMであり、個別環境への適用可否は未確認です。", pattern);
        Assert.DoesNotContain("利用可能です", pattern);
        Assert.DoesNotContain("正式サポート", pattern);
    }

    [Fact]
    public void ReleaseMigration_PreservesVersionRolesOperationAndConditionalTiming()
    {
        const string span = "Disabled checkers If you chose to migrate your projects_root directory, verify the checker configuration before your first integration build analysis .";
        var source = new SearchSource { SourceId = "official:release", SourceType = "OfficialDoc",
            Text = "Klocwork 2029.2 Release notes " + span };
        var request = Request("v2028.2からv2029.2へのバージョンアップを計画しています。2029.1は比較資料です。enabledフィールドの変更の有無を確認します。",
            [Official(source.SourceId, span)]);
        var prompt = GroundedAnswerPromptBuilder.Build(request, [source]);
        Assert.Contains("現在版・移行元=2028.2; 移行先=2029.2; 比較資料に言及した版=2029.1", prompt.UserPrompt);
        Assert.Contains("customerReplyDraftの冒頭", prompt.UserPrompt);
        Assert.Contains("version=2029.2 Release notes", prompt.UserPrompt);
        Assert.Contains("first integration build analysisより前", prompt.UserPrompt);
        Assert.Contains(span, prompt.UserPrompt);
        Assert.Contains("既定enabledフィールドの変更有無", prompt.UserPrompt);
    }

    [Fact]
    public void VersionedHistory_KeepsManufacturerOriginalUnverifiedInSameStatement()
    {
        var request = Request("9.8.2 HF1でＣＲＯＳヘッダを付与していない認識です。誤検知でしょうか。",
            [new ResolvedFact { Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                EvidenceId = "case:history", Value = "メーカーより9.8.7で不具合が修正されたとの回答がございました。" }]);
        var prompt = GroundedAnswerPromptBuilder.Build(request, [], "検証失敗: 帰属欠落");
        Assert.Contains("メーカー原文は未確認ですが、案件履歴には9.8.7で不具合が修正されたとのメーカー回答が記録されています。", prompt.UserPrompt);
        Assert.DoesNotContain("sourceType=OfficialDoc", prompt.UserPrompt);
        Assert.Contains("version=9.8.2", prompt.UserPrompt);
        Assert.Contains("顧客申告の既知事項を再質問しない", prompt.UserPrompt);
        Assert.Contains("ＣＲＯＳヘッダ", prompt.UserPrompt);
        Assert.DoesNotContain("CR-Oヘッダ", prompt.UserPrompt);
        var pattern = prompt.OutputSchema!.Value.GetProperty("properties").GetProperty("customerReplyDraft").GetProperty("pattern").GetString()!;
        const string required = "確認できる事実: メーカー原文は未確認ですが、案件履歴には9.8.7で不具合が修正されたとのメーカー回答が記録されています。お客様からは「ＣＲＯＳヘッダを付与していない」との申告があります。誤検知の可能性についてご相談をいただいています。お客様の対象版は9.8.2 HF1です。現時点で断定できない事項: 案件履歴に記録された修正の対象版・個別条件への適用は、メーカー原文が未確認のため断定できません。追加で必要な確認: 案件履歴のメーカー回答原文と、今回の対象版・検出条件への修正の適用範囲をメーカーへ確認してください。";
        Assert.Matches(pattern, required);
        Assert.Matches(pattern, required + "検出条件の詳細も確認してください。");
        Assert.DoesNotMatch(pattern, required + "9.8.7で修正されています。");
        Assert.DoesNotMatch(pattern, required + "CR-Oヘッダを確認してください。");
        Assert.DoesNotMatch(pattern, required.Replace("ＣＲＯＳヘッダ", "CR-Oヘッダ"));
        Assert.DoesNotMatch(pattern, required.Replace("メーカー原文は未確認ですが、案件履歴には", "公式資料では"));
        Assert.Equal(6, prompt.OutputSchema.Value.GetProperty("required").GetArrayLength());
        var confirmations = prompt.OutputSchema.Value.GetProperty("properties").GetProperty("needConfirmations");
        Assert.Equal(1, confirmations.GetProperty("minItems").GetInt32());
        Assert.Equal(1, confirmations.GetProperty("maxItems").GetInt32());
        Assert.Contains(confirmations.GetProperty("items").GetProperty("properties").GetProperty("question").GetProperty("const").GetString()!, required);
    }

    [Theory]
    [InlineData("PriorCaseOutcome", "ユーザーデータ領域変更後、約1週間再発しなかった。")]
    [InlineData("CaseObservation", "CxJobManager and CxSystemManager were stopped. After starting both services, the scan completed successfully.")]
    public void OrdinaryRecoveryContracts_DoNotEnterRelationalRewrite(string key, string value)
    {
        var request = Request("9.7.4.1001 HF5のログを確認したいです。",
            [new ResolvedFact { Key = key, Value = value, SourceType = "CurrentCase", Status = "Candidate" }]);
        Assert.False(GroundedAnswerPromptBuilder.NeedsRelationalContract(request.FactResolution));
        var prompt = GroundedAnswerPromptBuilder.Build(request, []);
        var unchanged = GroundedAnswerPromptBuilder.Build(request, [], "ignored focused repair");
        Assert.Equal(prompt.UserPrompt, unchanged.UserPrompt);
        Assert.Equal(prompt.OutputSchema!.Value.GetRawText(), unchanged.OutputSchema!.Value.GetRawText());
    }

    private static AnswerDraftRequest Request(string inquiry, IReadOnlyList<ResolvedFact> facts) => new()
    {
        Case = new CaseContext { ProductName = "Synthetic" }, InquiryText = inquiry,
        FactResolution = ImportantFactContract.Enrich(new FactResolutionResult
        { AnswerReadiness = "NeedsManufacturerConfirmation", ResolvedFacts = facts }, inquiry,
            new InquiryFocusExtractor().Extract(inquiry), new SafetyRedactionService()),
        Settings = new AiAssistantSettings { MaxPromptChars = 8000 },
    };

    private static ResolvedFact Official(string id, string value) => new()
    { Key = "SelectedOfficialStatement", SourceType = "OfficialDoc", Status = "Confirmed", EvidenceId = id, Value = value };
}
