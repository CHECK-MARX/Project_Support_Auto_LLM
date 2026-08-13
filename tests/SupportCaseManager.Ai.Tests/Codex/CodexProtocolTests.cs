using System.Text.Json;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexProtocolTests
{
    [Fact]
    public void SerializeRequest_MatchesJsonlProtocolShape()
    {
        var json = CodexProtocolSerializer.SerializeRequest(7, "turn/interrupt", new { threadId = "t1", turnId = "u1" });
        using var document = JsonDocument.Parse(json);

        Assert.Equal(7, document.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("turn/interrupt", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("t1", document.RootElement.GetProperty("params").GetProperty("threadId").GetString());
    }

    [Fact]
    public void Deserialize_RecognizesResponseNotificationAndServerRequest()
    {
        var response = CodexProtocolSerializer.Deserialize("{\"id\":1,\"result\":{\"ok\":true}}");
        var notification = CodexProtocolSerializer.Deserialize("{\"method\":\"turn/completed\",\"params\":{}}");
        var request = CodexProtocolSerializer.Deserialize("{\"id\":9,\"method\":\"item/fileChange/requestApproval\",\"params\":{}}");

        Assert.True(response.IsResponse);
        Assert.True(notification.IsNotification);
        Assert.True(request.IsServerRequest);
    }

    [Fact]
    public void Deserialize_InvalidJsonThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => CodexProtocolSerializer.Deserialize("not-json"));
    }
}
