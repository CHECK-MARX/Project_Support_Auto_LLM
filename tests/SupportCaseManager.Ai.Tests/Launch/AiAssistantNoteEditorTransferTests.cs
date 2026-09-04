using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Tests.Launch;

public sealed class AiAssistantNoteEditorTransferTests
{
    [Fact]
    public async Task SendAsync_TransfersUtf8TextToCurrentUserServer()
    {
        var pipeName = AiAssistantNoteEditorTransfer.CreatePipeName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new AiAssistantNoteEditorTransferServer(pipeName, text => received.TrySetResult(text));
        server.Start();

        var sent = await AiAssistantNoteEditorTransfer.SendAsync(pipeName, "技術回答案\r\n手順を確認してください。");

        Assert.True(sent);
        Assert.Equal("技術回答案\r\n手順を確認してください。", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SendAsync_ReturnsFalseWhenPipeIsNotConfigured()
    {
        Assert.False(await AiAssistantNoteEditorTransfer.SendAsync(null, "回答"));
        Assert.False(await AiAssistantNoteEditorTransfer.SendAsync("", "回答"));
        Assert.False(await AiAssistantNoteEditorTransfer.SendAsync("missing-pipe", ""));
    }
}
