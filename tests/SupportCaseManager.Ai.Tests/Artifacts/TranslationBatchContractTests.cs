using System.Text.Json;
using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class TranslationBatchContractTests
{
    [Fact]
    public void ExcelBatch_RejectsExtraSheetRenameEvenWhenAll80TranslationsMatch()
    {
        var entries = Enumerable.Range(1, 80).Select(i => new ExcelTranslationEntry
        {
            Sheet = "対象シート", Cell = $"A{i}", SourceText = $"確認{i}", ShouldTranslate = true,
        }).ToArray();
        var values = entries.Select(entry => new ExcelTranslationValue
        {
            Sheet = entry.Sheet, Cell = entry.Cell, SourceText = entry.SourceText, TranslatedText = "Review",
        }).ToList();
        values.Add(new() { Sheet = "別シート", Cell = "@sheet-name", SourceText = "別シート", TranslatedText = "Other" });
        var parser = new ExcelTranslationJsonParser();

        var invalid = parser.Parse(JsonSerializer.Serialize(values), entries);
        Assert.False(invalid.Succeeded);
        Assert.Empty(invalid.Values);
        Assert.Contains(invalid.Errors, error => error.Contains("計画にないセル"));

        values.RemoveAt(80);
        var corrected = parser.Parse(JsonSerializer.Serialize(values), entries);
        Assert.True(corrected.Succeeded);
        Assert.Equal(80, corrected.Values.Count);
    }

    [Fact]
    public void ExcelPrompt_DefinesCurrentBatchScopeWithoutFictitiousOutputTargets()
    {
        ExcelTranslationEntry[] entries =
        [
            new() { TargetKind = ExcelTranslationTargetKind.SheetName, Sheet = "対象", Cell = "@sheet-name", SourceText = "対象", ShouldTranslate = true },
            new() { Sheet = "対象", Cell = "C8", SourceText = "確認", ShouldTranslate = true },
        ];
        var prompt = new ArtifactPromptComposer().ComposeTranslationPrompt(new(), entries, new());
        Assert.Contains("Return exactly 2 objects", prompt);
        Assert.Contains("only when its SheetName item is explicitly present in THIS input JSON", prompt);
        Assert.DoesNotContain("Sheet1", prompt);
        Assert.DoesNotContain("B4", prompt);
        Assert.Contains("Do not return sourceText, sheet, cell", prompt);
        Assert.True(prompt.IndexOf("complete scope of this batch", StringComparison.Ordinal)
            > prompt.IndexOf("### 入力JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void TextPromptAndRepair_RequireCompleteCurrentBatchAndPreserveIdentifiers()
    {
        ArtifactTextTranslationEntry[] entries =
        [new() { Key = "paragraph:42", Location = "body", SourceText = "確認", ShouldTranslate = true }];
        var prompt = new ArtifactPromptComposer().ComposeTextTranslationPrompt(new(), entries, new());
        Assert.Contains("Return exactly 1 objects", prompt);
        Assert.Contains("Do not return sourceText, key", prompt);
        Assert.DoesNotContain("line:0", prompt);
        var errors = new[] { "計画にない項目: paragraph:100" };
        var repair = ArtifactPromptComposer.ComposeTranslationRepairPrompt(prompt, errors);
        Assert.StartsWith(prompt, repair);
        Assert.Equal(1, repair.Split("### 入力JSON", StringSplitOptions.None).Length - 1);
        Assert.Contains(JsonSerializer.Serialize(errors), repair);
        Assert.Contains("差分だけでなく全項目", repair);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[]")]
    [InlineData("[{\"sheet\":\"S\",\"cell\":\"A1\",\"sourceText\":\"改変\",\"translatedText\":\"Review\"}]")]
    [InlineData("[{\"sheet\":\"S\",\"cell\":\"A1\",\"sourceText\":\"確認\",\"translatedText\":\"\"}]")]
    [InlineData("[{\"sheet\":\"S\",\"cell\":\"A1\",\"sourceText\":\"確認\",\"translatedText\":\"Review\"},{\"sheet\":\"S\",\"cell\":\"A1\",\"sourceText\":\"確認\",\"translatedText\":\"Review\"}]")]
    public void ExcelValidation_RemainsStrictForEveryAttempt(string response)
    {
        var result = new ExcelTranslationJsonParser().Parse(response,
            [new() { Sheet = "S", Cell = "A1", SourceText = "確認", ShouldTranslate = true }]);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Values);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"key\":null,\"sourceText\":\"確認\",\"translatedText\":\"Review\"}]")]
    [InlineData("[{\"key\":\"other\",\"sourceText\":\"確認\",\"translatedText\":\"Review\"}]")]
    public void TextValidation_RejectsNullAndUnrequestedItems(string response)
    {
        var result = new ArtifactTextTranslationJsonParser().Parse(response,
            [new() { Key = "target", SourceText = "確認", ShouldTranslate = true }]);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }
    [Fact]
    public void IdentifiedResponse_PreservesLocalSourceIncludingTypoWhitespaceAndNewlines()
    {
        ExcelTranslationEntry[] entries =
        [
            new() { Sheet = "追加確認事項", Cell = "A2", SourceText = "確認事項を記述しています。\r\n変更しない。", ShouldTranslate = true },
            new() { Sheet = "別", Cell = "D32", SourceText = "IsCorrectPath)ギョウゾン\t確認", ShouldTranslate = true },
        ];
        var response = JsonSerializer.Serialize(entries.Reverse().Select(entry => new
        {
            id = ArtifactTranslationBatchContract.Id(entry), translatedText = $"Translation for {entry.Cell}",
        }));
        Assert.DoesNotContain("sourceText", response);
        var result = new ExcelTranslationJsonParser().ParseIdentified(response, entries);
        Assert.True(result.Succeeded);
        Assert.Equal(entries.Select(x => x.SourceText), result.Values.Select(x => x.SourceText));
        Assert.Equal(entries.Select(x => x.Cell), result.Values.Select(x => x.Cell));
        Assert.Equal("Translation for A2", result.Values[0].TranslatedText);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("oldSource")]
    [InlineData("oldCell")]
    [InlineData("echoSource")]
    [InlineData("null")]
    [InlineData("invalidJson")]
    public void IdentifiedResponse_RejectsBrokenIdentityOrContract(string corruption)
    {
        var entry = new ExcelTranslationEntry { Sheet = "S", Cell = "A2", SourceText = "記述", ShouldTranslate = true };
        var id = ArtifactTranslationBatchContract.Id(entry);
        var valid = new { id, translatedText = "Described" };
        var response = corruption switch
        {
            "extra" => JsonSerializer.Serialize(new[] { valid, new { id = "unrequested", translatedText = "Other" } }),
            "missing" => "[]",
            "duplicate" => JsonSerializer.Serialize(new[] { valid, valid }),
            "blank" => JsonSerializer.Serialize(new[] { new { id, translatedText = " " } }),
            "oldSource" => JsonSerializer.Serialize(new[] { new { id = ArtifactTranslationBatchContract.Id(entry with { SourceText = "記載" }), translatedText = "Described" } }),
            "oldCell" => JsonSerializer.Serialize(new[] { new { id = ArtifactTranslationBatchContract.Id(entry with { Cell = "A3" }), translatedText = "Described" } }),
            "echoSource" => JsonSerializer.Serialize(new[] { new { id, translatedText = "Described", sourceText = "記載" } }),
            "null" => "[null]",
            _ => "invalid JSON",
        };
        var result = new ExcelTranslationJsonParser().ParseIdentified(response, [entry]);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void IdentifiedTextResponse_PreservesApprovedSourceAndRejectsOtherBatch()
    {
        var entry = new ArtifactTextTranslationEntry { Key = "line:2", SourceText = "記述しています。", ShouldTranslate = true };
        var response = JsonSerializer.Serialize(new[] { new { id = ArtifactTranslationBatchContract.Id(entry), translatedText = "Described." } });
        var parser = new ArtifactTextTranslationJsonParser();
        var result = parser.ParseIdentified(response, [entry]);
        Assert.True(result.Succeeded);
        Assert.Equal(entry.SourceText, Assert.Single(result.Values).SourceText);
        Assert.False(parser.ParseIdentified(response, [entry with { Key = "line:3" }]).Succeeded);
    }

}
