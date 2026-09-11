using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerDraftPairParserTests
{
    [Fact]
    public void ParseAndValidate_PreservesQuestionsAttachmentsAndTechnicalValues()
    {
        const string response = """{"japaneseDraft":"件名: CxSAST 9.7.6の確認\nサポートID: 00018290\n添付: 問い合わせ内容.xlsx\n質問1: qacli analyzeの結果を確認してください。","englishDraft":"Subject: CxSAST 9.7.6 review\nSupport ID: 00018290\nAttachment: 問い合わせ内容.xlsx\nHello Support Team,\nQuestion 1: Please confirm the qacli analyze result."}""";
        var parser = new ManufacturerDraftPairParser();

        var parsed = parser.Parse(response);
        Assert.True(parsed.Succeeded);
        Assert.NotNull(parsed.Pair);

        var validation = parser.Validate(
            parsed.Pair!,
            ["問い合わせ内容.xlsx"],
            ["CxSAST", "9.7.6", "00018290", "qacli analyze"]);

        Assert.True(validation.Succeeded, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void Parse_RejectsMissingLanguageDraft()
    {
        var parsed = new ManufacturerDraftPairParser().Parse(
            "{\"japaneseDraft\":\"日本語案\",\"englishDraft\":\"\"}");

        Assert.False(parsed.Succeeded);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void Validate_RejectsQuestionOrAttachmentParityMismatch()
    {
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "質問1: A\n添付: source.txt",
                EnglishDraft = "Question 2: A\n添付: other.txt",
            },
            ["source.txt"],
            []);

        Assert.False(validation.Succeeded);
        Assert.Equal(2, validation.Errors.Count);
    }

    [Fact]
    public void CanonicalProtectedValueSet_IsSharedByPromptAndValidator()
    {
        var parser = new ManufacturerDraftPairParser();
        var source = string.Join(
            " ",
            "GetSecurePath",
            "GetSecureName",
            "GetSecureFileName",
            "IsCorrectPath",
            "Path.GetFileName()",
            "basePath",
            "combinePath",
            "9.7.4.1008",
            "HF9");
        var protectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet(
            "Checkmarx",
            "00018742",
            "Additional_Inquiry_Details_EN.xlsx",
            ["KSAY0005207.csv"],
            source);

        var expected = new[]
        {
            "GetSecurePath",
            "GetSecureName",
            "GetSecureFileName",
            "IsCorrectPath",
            "Path.GetFileName()",
            "basePath",
            "combinePath",
            "9.7.4.1008",
            "HF9",
            "KSAY0005207",
            "Checkmarx",
            "00018742",
            "Additional_Inquiry_Details_EN.xlsx",
        };

        Assert.Equal(expected.Length, protectedValues.Count);
        Assert.All(expected, value => Assert.Contains(value, protectedValues.Values));
        Assert.DoesNotContain("ordinary", protectedValues.Values, StringComparer.Ordinal);

        var body = string.Join(
            Environment.NewLine,
            protectedValues.Values.Select(static value => $"{value} を確認します。"));
        var validation = parser.Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = $"質問1: {body}\n添付: Additional_Inquiry_Details_EN.xlsx",
                EnglishDraft = $"Question 1: {body}\nAttachment: Additional_Inquiry_Details_EN.xlsx",
            },
            protectedValues,
            ["Additional_Inquiry_Details_EN.xlsx"]);

        Assert.True(validation.Succeeded, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(protectedValues.Values.Count, protectedValues.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Validate_ReportsLanguageSpecificProtectedValueMissing()
    {
        var parser = new ManufacturerDraftPairParser();
        var protectedValues = new ManufacturerProtectedValueSet
        {
            Items =
            [
                new() { Literal = "GetSecurePath" },
                new() { Literal = "Path.GetFileName()" },
                new() { Literal = "00018742" },
            ],
        };
        var validation = parser.Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "質問1: GetSecurePath Path.GetFileName() 00018742",
                EnglishDraft = "Question 1: GetSecurePath 00018742",
            },
            protectedValues,
            []);

        Assert.False(validation.Succeeded);
        Assert.True(validation.QuestionParity);
        Assert.True(validation.AttachmentParity);
        Assert.False(validation.TechnicalParity);
        Assert.Empty(validation.MissingJapaneseProtectedValues);
        Assert.Equal(["Path.GetFileName()"], validation.MissingEnglishProtectedValues);
        Assert.Contains("技術値が日英案の両方に保持されていません: Path.GetFileName()", validation.Errors);
    }

    [Fact]
    public void Validate_ReportsJapaneseProtectedValueMissing()
    {
        var protectedValues = new ManufacturerProtectedValueSet
        {
            Items = [new() { Literal = "GetSecurePath" }],
        };
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "質問1: 内容を確認します。",
                EnglishDraft = "Question 1: GetSecurePath will be verified.",
            },
            protectedValues,
            []);

        Assert.False(validation.Succeeded);
        Assert.Empty(validation.MissingEnglishProtectedValues);
        Assert.Equal(["GetSecurePath"], validation.MissingJapaneseProtectedValues);
        Assert.Contains("技術値が日英案の両方に保持されていません: GetSecurePath", validation.Errors);
    }

    [Fact]
    public void Validate_KeepsAttachmentFailureOutOfTechnicalParity()
    {
        var protectedValues = new ManufacturerProtectedValueSet
        {
            Items = [new() { Literal = "Additional_Inquiry_Details_EN.xlsx", Category = "Attachment" }],
        };
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "質問1: 添付を確認します。",
                EnglishDraft = "Question 1: Please review the attachment.",
            },
            protectedValues,
            ["Additional_Inquiry_Details_EN.xlsx"]);

        Assert.False(validation.Succeeded);
        Assert.False(validation.AttachmentParity);
        Assert.True(validation.TechnicalParity);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", validation.MissingJapaneseProtectedValues);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", validation.MissingEnglishProtectedValues);
        Assert.DoesNotContain(validation.Errors, error => error.StartsWith("技術値が", StringComparison.Ordinal));
    }
}
