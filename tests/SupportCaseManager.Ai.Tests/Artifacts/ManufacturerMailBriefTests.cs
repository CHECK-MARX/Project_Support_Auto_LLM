using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerMailBriefTests
{
    private static ManufacturerSafeContext Context() => new()
    {
        DraftMode = ManufacturerDraftMode.FollowUp,
        ProductName = "Scanner",
        SupportId = "TEST-42",
        CurrentOutboundAttachments = ["Additional_Inquiry_Details_EN.xlsx"],
        CurrentCustomerDeltaTechnicalContent =
        [
            "お客様はparameter-based designを維持したい。",
            "パラメータを使用しない方式は修正コストが大きいため避けたい。",
            "GetSecurePath、GetSecureName、GetSecureFileNameで安全に実装したい。",
            "QueryにSanitizerとして認識させたい。",
            "質問1：parameter-based designを維持し、GetSecurePath、GetSecureName、GetSecureFileNameをSanitizerとして実装する方法をご教示ください。",
            "質問2：推奨C#実装例をご教示ください。",
            "質問3：itemとrevを結合後にPath.GetFileName()を使う処理を確認してください。",
            "質問4：IsCorrectPathの使用方法をご教示ください。",
            "質問5：basePathとcombinePathに異なる値を指定するとQueryがcontrol-flowを自動的に検証として認識しますか？",
            "質問6：CxAuditとCxQLのカスタマイズなしで実装とSanitizer認識を実現できますか？",
        ],
        RelevantPriorManufacturerResponse =
        [
            "動的データはファイル名だけに使うことを推奨します。",
            "GetSecurePath、GetSecureName、GetSecureFileNameは十分な緩和ではなく、Sanitizerとして推奨されない。",
            "itemとrevを結合後にPath.GetFileName()を使います。",
            "IsCorrectPathのBoolean control-flow検証自体は問題ありません。",
            "basePathとcombinePathが同じ値であることが問題です。",
            "CxAuditとCxQLのカスタマイズは不要です。",
            "以前はプリセットから除外する別件も扱いました。",
        ],
        PriorTechnicalContext = "別の生成案: 納期が迫っている。OLD.csvを再送する。",
    };

    private static ManufacturerDraftPair Golden() => new()
    {
        JapaneseDraft = """
            件名：Scannerの追加確認（Support ID: TEST-42）
            メーカーサポートご担当者様
            お世話になっております。東陽テクニカの伊藤です。
            前回はご回答ありがとうございました。お客様にご案内後、追加質問を受領しました。
            動的データはファイル名だけに使うとのご推奨でした。
            GetSecurePath、GetSecureName、GetSecureFileNameは十分な緩和ではなくSanitizerとして推奨されないと承知しました。
            itemとrevを結合後にPath.GetFileName()を使うこと、IsCorrectPathのBoolean control-flow検証自体は問題ないこと、basePathとcombinePathが同じ値であることが問題で、CxAuditとCxQLのカスタマイズは不要とのご回答でした。
            お客様はparameter-based designを維持したいと希望しています。パラメータを使わない方式への修正コストを避けたいとのことです。
            GetSecurePath、GetSecureName、GetSecureFileNameで安全に実装し、QueryにSanitizerとして認識させたいとのご希望です。

            質問1：parameter-based designを維持し、GetSecurePath、GetSecureName、GetSecureFileNameをSanitizerとして実装する方法をご教示ください。

            質問2：C#の実装例をお願いできますか。

            質問3：itemとrevを結合後にPath.GetFileName()を使う処理をご確認ください。

            質問4：IsCorrectPathの使い方をご教示ください。

            質問5：basePathとcombinePathに異なる値を指定した場合、Queryがcontrol-flowを自動的に検証として認識しますか。

            質問6：CxAuditとCxQLのカスタマイズなしで実装とSanitizer認識を実現できますか。

            Additional_Inquiry_Details_EN.xlsxを添付します。
            どうぞよろしくお願いいたします。
            株式会社東陽テクニカ
            伊藤 健
            """,
        EnglishDraft = """
            Subject: Follow-up questions for Scanner (Support ID: TEST-42)
            Hello Support Team,
            This is Ken Ito from Toyo Corporation. Thank you for your previous response. After sharing it with our customer, we received additional questions.
            You recommended using dynamic data only in filenames.
            We understand that GetSecurePath, GetSecureName and GetSecureFileName are not sufficient mitigation and are not recommended as sanitizers.
            You recommended combining item and rev, then using Path.GetFileName(). The Boolean control-flow validation of IsCorrectPath is acceptable, but basePath and combinePath have the same value. CxAudit and CxQL customization is not required.
            Our customer would like to retain the parameter-based design and avoid the cost of changing away from parameters. They want to implement secure processing with GetSecurePath, GetSecureName and GetSecureFileName and have the Query recognize it as a sanitizer.

            Question 1: How can they retain the parameter-based design and implement GetSecurePath, GetSecureName and GetSecureFileName as sanitizers?

            Question 2: Could you provide a C# implementation example?

            Question 3: Please confirm combining item and rev and then applying Path.GetFileName().

            Question 4: Could you explain the proper use of IsCorrectPath?

            Question 5: With different values for basePath and combinePath, will the Query automatically recognize the control-flow as validation?

            Question 6: Can the implementation and sanitizer recognition be achieved without CxAudit and CxQL customization?

            We attach Additional_Inquiry_Details_EN.xlsx.
            Best regards,
            Ken Ito
            Toyo Corporation
            """,
    };

    [Fact]
    public void Brief_UsesOnlyCurrentQuestionsAndRelatedPriorResponse()
    {
        var brief = ManufacturerMailBriefBuilder.Build(Context());
        Assert.True(brief.Ready);
        Assert.Equal("CUSTOMER", brief.OriginRole);
        Assert.Equal("CUSTOMER_ADDITIONAL_QUESTION", brief.RequestSource);
        Assert.Equal("MANUFACTURER", brief.RecipientRole);
        Assert.Equal("FOLLOW_UP_TO_MANUFACTURER_RESPONSE", brief.Purpose);
        Assert.Equal("CUSTOMER_ADDITIONAL_QUESTIONS", brief.FollowUpTrigger);
        Assert.Equal(6, brief.CurrentQuestions.Count);
        Assert.Equal(4, brief.CurrentCustomerIntent.Count);
        Assert.Equal(3, brief.PriorResponseSummaryPoints.Count);
        Assert.Equal(["Additional_Inquiry_Details_EN.xlsx"], brief.CurrentOutboundAttachments);
        var prompt = new ManufacturerMailComposer().ComposeBilingualPrompt("", "Additional_Inquiry_Details_EN.xlsx", "", Context());
        Assert.DoesNotContain("OLD.csv", prompt);
        Assert.DoesNotContain("納期が迫", prompt);
        Assert.DoesNotContain("以前はプリセット", prompt);
    }

    [Fact]
    public void Golden_ConceptCoverageAndFormalStructurePassInBothLanguages()
    {
        var result = new ManufacturerMailQualityValidator().Validate(Golden(), Context());
        Assert.True(result.Succeeded, string.Join("\n", result.Issues));
        Assert.Equal(6, result.Semantic.CoveredQuestions);
        Assert.Equal(0, result.Semantic.CoveredPrior);
        Assert.Equal(0, result.Semantic.CoveredIntent);
    }

    [Fact]
    public void BadGuiRegression_IsRejectedDespiteFormalStructure()
    {
        var golden = Golden();
        var bad = golden with
        {
            JapaneseDraft = golden.JapaneseDraft.Replace("質問1：parameter-based designを維持し、GetSecurePath、GetSecureName、GetSecureFileNameをSanitizerとして実装する方法をご教示ください。",
                "質問1：どのような実装情報が必要でしょうか。プリセットからPath Traversalを除外する方法はありますか。")
                + "\n添付はOLD.csv、OLD.pdfです。納期が迫っているため回答願います。",
        };
        var result = new ManufacturerMailQualityValidator().Validate(bad, Context());
        Assert.False(result.Succeeded);
        Assert.Contains("AttachmentExactMatch.Japanese", result.Issues);
        Assert.Contains("ForbiddenTopic.deadline", result.Issues);
        Assert.Contains("ForbiddenTopic.preset-exclusion", result.Issues);
    }

    [Fact]
    public void Coverage_CannotBeSuppliedByOtherQuestionsOrSignature()
    {
        var golden = Golden();
        var result = new ManufacturerMailQualityValidator().Validate(golden with
        {
            EnglishDraft = golden.EnglishDraft.Replace("IsCorrectPath", "")
                .Replace("Question 4: Could you explain the proper use of ?", "Question 4: Please advise.")
                + "\nIsCorrectPath",
        }, Context());
        Assert.Contains("MajorTechnicalTopicCoverage.IsCorrectPath", result.Issues);
    }

    [Fact]
    public void MissingDelta_DoesNotFallBackToGeneratedTechnicalAnswer()
    {
        var context = Context() with { CurrentCustomerDeltaTechnicalContent = [] };
        Assert.False(ManufacturerMailBriefBuilder.Build(context).Ready);
    }

    [Fact]
    public void Brief_AllowsWorkflowFollowUpWhenResponseBodyIsUnavailable()
    {
        var context = Context() with
        {
            RelevantPriorManufacturerResponse = [],
            PreviousManufacturerContactConfirmed = true,
            PreviousCustomerReplyFound = true,
        };

        var brief = ManufacturerMailBriefBuilder.Build(context);

        Assert.True(brief.Ready);
        Assert.Equal(ManufacturerDraftMode.FollowUp, brief.Mode);
        Assert.Empty(brief.PriorResponseSummaryPoints);
    }

    [Theory]
    [InlineData("特に問題がなければ、本案件をクローズさせていただきたい")]
    [InlineData("Please close this case.")]
    [InlineData("We would like to close the case.")]
    public void CloseIntent_RequiresCurrentCustomerDelta(string close)
    {
        var context = Context() with { PriorTechnicalContext = close };
        var brief = ManufacturerMailBriefBuilder.Build(context);
        Assert.False(brief.CloseRequested);
        Assert.DoesNotContain(brief.CurrentCustomerIntent, item => item.Text.Contains(close));
        var pair = Golden() with { JapaneseDraft = Golden().JapaneseDraft + "\n" + close };
        Assert.Contains("UnsupportedCustomerIntent.CloseCase",
            new ManufacturerMailSemanticValidator().Validate(pair, brief).Issues);
        Assert.Contains("ForbiddenTopic.close-intent",
            new ManufacturerMailSemanticValidator().Validate(pair, brief).Issues);
        var requested = ManufacturerMailBriefBuilder.Build(context with
        {
            CurrentCustomerDeltaTechnicalContent = [.. context.CurrentCustomerDeltaTechnicalContent, close],
        });
        Assert.True(requested.CloseRequested);
        Assert.DoesNotContain("UnsupportedCustomerIntent.CloseCase",
            new ManufacturerMailSemanticValidator().Validate(pair, requested).Issues);
    }

    [Theory]
    [InlineData("Do not close this case.")]
    [InlineData("本案件をクローズすることは希望しない。")]
    public void NegativeCloseRequest_DoesNotAuthorizeClosure(string text)
    {
        var context = Context();
        Assert.False(ManufacturerMailBriefBuilder.Build(context with
        {
            CurrentCustomerDeltaTechnicalContent = [.. context.CurrentCustomerDeltaTechnicalContent, text],
        }).CloseRequested);
    }
}
