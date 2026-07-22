using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);

string? activeThread = null;
string? activeTurn = null;
while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    if (!root.TryGetProperty("method", out var methodElement))
    {
        continue;
    }

    var method = methodElement.GetString() ?? string.Empty;
    var hasId = root.TryGetProperty("id", out var id);
    switch (method)
    {
        case "initialize":
            await WriteResponseAsync(id, new
            {
                codexHome = "C:/fake-codex",
                platformFamily = "windows",
                platformOs = "windows",
                userAgent = "fake-codex/0.145.0",
            });
            break;
        case "initialized":
            break;
        case "account/read":
            await WriteResponseAsync(id, new
            {
                account = new { type = "chatgpt", email = (string?)null, planType = "plus" },
                requiresOpenaiAuth = true,
            });
            break;
        case "model/list":
            await WriteResponseAsync(id, new
            {
                data = new[]
                {
                    new
                    {
                        id = "fake-default",
                        model = "fake-default",
                        displayName = "Fake Default",
                        description = "Fake model",
                        isDefault = true,
                        hidden = false,
                        defaultReasoningEffort = "medium",
                        supportedReasoningEfforts = Array.Empty<object>(),
                    },
                },
                nextCursor = (string?)null,
            });
            break;
        case "thread/start":
        case "thread/resume":
            activeThread = method == "thread/resume"
                ? root.GetProperty("params").GetProperty("threadId").GetString()
                : "fake-thread-1";
            var cwd = root.GetProperty("params").GetProperty("cwd").GetString() ?? "C:/case";
            await WriteResponseAsync(id, new
            {
                thread = new { id = activeThread, turns = Array.Empty<object>() },
                model = "fake-default",
                cwd,
                sandbox = "read-only",
                approvalPolicy = "never",
                approvalsReviewer = "user",
                modelProvider = "openai",
            });
            break;
        case "turn/start":
            activeTurn = $"fake-turn-{Guid.NewGuid():N}";
            var text = ReadInputText(root.GetProperty("params"));
            await WriteResponseAsync(id, new
            {
                turn = new { id = activeTurn, status = "inProgress", items = Array.Empty<object>(), error = (object?)null },
            });
            await WriteNotificationAsync("turn/started", new
            {
                threadId = activeThread,
                turn = new { id = activeTurn, status = "inProgress", items = Array.Empty<object>(), error = (object?)null },
            });
            if (text.Contains("EXIT_PROCESS", StringComparison.Ordinal))
            {
                await Console.Out.FlushAsync();
                Environment.Exit(17);
            }
            if (!text.Contains("WAIT_FOR_INTERRUPT", StringComparison.Ordinal))
            {
                await WriteNotificationAsync("item/agentMessage/delta", new
                {
                    threadId = activeThread,
                    turnId = activeTurn,
                    itemId = "agent-1",
                    delta = "Fake回答",
                });
                await WriteNotificationAsync("item/agentMessage/delta", new
                {
                    threadId = activeThread,
                    turnId = activeTurn,
                    itemId = "agent-1",
                    delta = "です。",
                });
                await WriteNotificationAsync("turn/completed", new
                {
                    threadId = activeThread,
                    turn = new { id = activeTurn, status = "completed", items = Array.Empty<object>(), error = (object?)null },
                });
            }
            break;
        case "turn/interrupt":
            await WriteResponseAsync(id, new { });
            await WriteNotificationAsync("turn/completed", new
            {
                threadId = activeThread,
                turn = new { id = activeTurn, status = "interrupted", items = Array.Empty<object>(), error = (object?)null },
            });
            break;
        default:
            if (hasId)
            {
                await WriteErrorAsync(id, -32601, $"Unknown method: {method}");
            }
            break;
    }
}

static string ReadInputText(JsonElement parameters)
{
    if (!parameters.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
    {
        return string.Empty;
    }

    return string.Join(
        Environment.NewLine,
        input.EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(item => item.GetProperty("text").GetString()));
}

static Task WriteResponseAsync(JsonElement id, object result)
{
    return WriteAsync(new JsonObject
    {
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["result"] = JsonSerializer.SerializeToNode(result),
    });
}

static Task WriteErrorAsync(JsonElement id, int code, string message)
{
    return WriteAsync(new JsonObject
    {
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    });
}

static Task WriteNotificationAsync(string method, object parameters)
{
    return WriteAsync(new JsonObject
    {
        ["method"] = method,
        ["params"] = JsonSerializer.SerializeToNode(parameters),
    });
}

static async Task WriteAsync(JsonObject message)
{
    await Console.Out.WriteLineAsync(message.ToJsonString());
    await Console.Out.FlushAsync();
}

public sealed class FakeServerMarker;
