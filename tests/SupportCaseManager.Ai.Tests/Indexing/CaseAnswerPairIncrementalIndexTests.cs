using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class CaseAnswerPairIncrementalIndexTests
{
    [Fact]
    public async Task IncrementalUpdate_AddsOnlyNewReplyAndSkipsUnchangedCase()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "closed");
        var index = Path.Combine(temp.Path, "index");
        var caseFolder = CreateCaseFolder(source);
        var questionPath = Path.Combine(caseFolder, "お客様ご相談内容_00001234.txt");
        await File.WriteAllTextAsync(questionPath, "ERR-42について教えてください。", Encoding.UTF8);
        var builder = CreateBuilder();

        var first = await builder.BuildIncrementalForProductAsync(source, index, "HelixQAC");
        var replyPath = Path.Combine(caseFolder, "お客様への返信案_00001234.txt");
        await File.WriteAllTextAsync(replyPath, "設定Aをご確認ください。", Encoding.UTF8);
        File.SetLastWriteTime(replyPath, DateTime.Now.AddSeconds(2));
        var addedReply = await builder.BuildIncrementalForProductAsync(source, index, "HelixQAC");
        var unchanged = await builder.BuildIncrementalForProductAsync(source, index, "HelixQAC");
        var document = await ReadAsync(index);

        Assert.Equal(0, first.IndexedAnswerPairCount);
        Assert.Equal(1, addedReply.ChangedCaseCount);
        Assert.Equal(1, addedReply.IndexedAnswerPairCount);
        Assert.Equal(1, unchanged.UnchangedCaseCount);
        Assert.Single(document.Pairs);
        Assert.Equal("設定Aをご確認ください。", document.Pairs[0].CustomerReplyText);
    }

    [Fact]
    public async Task IncrementalUpdate_DeletesPairWhenReplyIsDeleted()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "closed");
        var index = Path.Combine(temp.Path, "index");
        var caseFolder = CreateCaseFolder(source);
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "お客様ご相談内容_00001234.txt"), "質問", Encoding.UTF8);
        var replyPath = Path.Combine(caseFolder, "お客様への返信案_00001234.txt");
        await File.WriteAllTextAsync(replyPath, "回答", Encoding.UTF8);
        var builder = CreateBuilder();
        _ = await builder.BuildIncrementalForProductAsync(source, index, "HelixQAC");

        File.Delete(replyPath);
        var result = await builder.BuildIncrementalForProductAsync(source, index, "HelixQAC");

        Assert.Equal(1, result.ChangedCaseCount);
        Assert.Empty((await ReadAsync(index)).Pairs);
    }

    [Fact]
    public async Task IncrementalUpdate_WhenChangedCaseReadFails_RetainsPreviousPair()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "closed");
        var index = Path.Combine(temp.Path, "index");
        var caseFolder = CreateCaseFolder(source);
        var notePath = Path.Combine(caseFolder, "note.txt");
        await File.WriteAllTextAsync(notePath, "initial", Encoding.UTF8);
        var contextBuilder = new FlakyContextBuilder(caseFolder);
        var builder = new AiCaseIndexBuilder(contextBuilder);
        _ = await builder.BuildIncrementalForProductAsync(source, index, "HelixQAC");

        contextBuilder.Throw = true;
        await File.WriteAllTextAsync(notePath, "changed", Encoding.UTF8);
        File.SetLastWriteTime(notePath, DateTime.Now.AddSeconds(3));
        var failed = await builder.BuildIncrementalForProductAsync(source, index, "HelixQAC");
        var retained = Assert.Single((await ReadAsync(index)).Pairs);

        Assert.Equal(1, failed.ErrorCount);
        Assert.Equal("previous answer", retained.CustomerReplyText);
    }

    private static AiCaseIndexBuilder CreateBuilder()
    {
        return new AiCaseIndexBuilder(new CaseContextBuilder(new NoteSnapshotReader()));
    }

    private static string CreateCaseFolder(string source)
    {
        var folder = Path.Combine(source, "20260719(株式会社サンプル_00001234)対応中_20260719");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task<CaseAnswerPairIndexDocument> ReadAsync(string indexFolder)
    {
        await using var stream = File.OpenRead(Path.Combine(indexFolder, CaseAnswerPairIndexDocument.FileName));
        return await JsonSerializer.DeserializeAsync<CaseAnswerPairIndexDocument>(stream)
            ?? throw new InvalidOperationException("Answer pair index could not be read.");
    }

    private sealed class FlakyContextBuilder : ICaseContextBuilder
    {
        private readonly string caseFolder;

        public FlakyContextBuilder(string caseFolder)
        {
            this.caseFolder = caseFolder;
        }

        public bool Throw { get; set; }

        public Task<CaseContext> BuildFromCaseFolderAsync(
            string caseFolderPath,
            string? productName = null,
            string? baseFolder = null,
            string? closeFolder = null,
            CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new IOException("simulated read failure");
            }

            return Task.FromResult(new CaseContext
            {
                ProductName = productName,
                SupportNumber = "00001234",
                Notes =
                [
                    new NoteSnapshot { NoteKind = "お客様ご相談内容", FilePath = Path.Combine(caseFolder, "question.txt"), Text = "previous question" },
                    new NoteSnapshot { NoteKind = "お客様への返信案", FilePath = Path.Combine(caseFolder, "reply.txt"), Text = "previous answer" },
                ],
            });
        }
    }
}
