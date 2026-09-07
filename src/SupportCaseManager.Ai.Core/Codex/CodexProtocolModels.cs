using System.Text.Json;
using System.Text.Json.Nodes;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record CodexIncomingMessage
{
    public string? Id { get; init; }
    public string? Method { get; init; }
    public JsonElement? Params { get; init; }
    public JsonElement? Result { get; init; }
    public CodexJsonRpcError? Error { get; init; }
    public bool IsResponse => Id is not null && (Result.HasValue || Error is not null);
    public bool IsServerRequest => Id is not null && Method is not null;
    public bool IsNotification => Id is null && Method is not null;
}

public sealed record CodexJsonRpcError(int Code, string Message, JsonElement? Data = null);

public sealed class CodexJsonRpcException : Exception
{
    public CodexJsonRpcException(int code, string message)
        : base($"Codex App Serverエラー ({code}): {message}")
    {
        Code = code;
    }

    public int Code { get; }
}

public static class CodexProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string SerializeRequest(long id, string method, object? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters, Options),
        };
        return message.ToJsonString(Options);
    }

    public static string SerializeNotification(string method, object? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var message = new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters, Options),
        };
        return message.ToJsonString(Options);
    }

    public static string SerializeResponse(string id, object? result = null, CodexJsonRpcError? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var idNode = long.TryParse(id, out var numericId)
            ? JsonValue.Create(numericId)
            : JsonValue.Create(id);
        var message = new JsonObject { ["id"] = idNode };
        if (error is null)
        {
            message["result"] = result is null ? new JsonObject() : JsonSerializer.SerializeToNode(result, Options);
        }
        else
        {
            message["error"] = JsonSerializer.SerializeToNode(error, Options);
        }

        return message.ToJsonString(Options);
    }

    public static CodexIncomingMessage Deserialize(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            throw new JsonException("Codex App Serverから空のJSONメッセージを受信しました。");
        }

        using var document = JsonDocument.Parse(jsonLine);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Codex App ServerメッセージがJSON objectではありません。");
        }

        string? id = null;
        if (root.TryGetProperty("id", out var idElement))
        {
            id = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.GetRawText(),
                _ => null,
            };
        }

        var method = root.TryGetProperty("method", out var methodElement)
            && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()
            : null;
        JsonElement? parameters = root.TryGetProperty("params", out var paramsElement)
            ? paramsElement.Clone()
            : null;
        JsonElement? result = root.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : null;
        CodexJsonRpcError? error = null;
        if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : -1;
            var message = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "Unknown error"
                : "Unknown error";
            error = new CodexJsonRpcError(
                code,
                message,
                errorElement.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : null);
        }

        return new CodexIncomingMessage
        {
            Id = id,
            Method = method,
            Params = parameters,
            Result = result,
            Error = error,
        };
    }
}

public sealed record CodexAccountInfo
{
    public bool RequiresOpenAiAuth { get; init; }
    public string AccountType { get; init; } = string.Empty;
    public string PlanType { get; init; } = string.Empty;
    public bool IsChatGptAuthenticated => string.Equals(AccountType, "chatgpt", StringComparison.OrdinalIgnoreCase);
}

public sealed record CodexReasoningEffortInfo(string Value, string Description)
{
    public string DisplayName => CodexReasoningEffortDisplay.Format(Value, Description);
}

public static class CodexReasoningEffortDisplay
{
    public static string Format(string value, string? description = null)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var japanese = normalized.ToLowerInvariant() switch
        {
            "low" => "低 ― 高速・軽量",
            "medium" => "中 ― 標準・バランス重視",
            "high" => "高 ― 複雑な調査向け",
            "xhigh" => "超高 ― 高度な推論が必要な問題向け",
            "max" => "最大 ― 最大限の推論",
            "ultra" => "最上位 ― 最大推論＋自動タスク委譲",
            _ => string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(japanese))
        {
            return japanese;
        }

        return string.IsNullOrWhiteSpace(description)
            ? normalized
            : $"{normalized} - {description.Trim()}";
    }
}

public sealed record CodexCaseModelOption(
    string Id,
    string DisplayName,
    bool IsInherit = false);

public sealed record CodexCaseReasoningEffortOption(
    string Id,
    string DisplayName,
    bool IsInherit = false);

public sealed record CodexModelInfo(
    string Id,
    string DisplayName,
    bool IsDefault,
    bool Hidden,
    string DefaultReasoningEffort = "",
    IReadOnlyList<CodexReasoningEffortInfo>? SupportedReasoningEfforts = null)
{
    public string SelectionDisplayName => string.IsNullOrWhiteSpace(DisplayName) || string.Equals(DisplayName, Id, StringComparison.OrdinalIgnoreCase)
        ? Id
        : $"{DisplayName} ({Id})";

    public IReadOnlyList<CodexReasoningEffortInfo> ReasoningEfforts => SupportedReasoningEfforts ?? [];
}

public sealed record CodexThreadStartResult(
    string ThreadId,
    string Model,
    string WorkingDirectory,
    string Sandbox,
    string? LastTurnId = null,
    string ReasoningEffort = "");

public sealed record CodexTurnStartResult(
    string TurnId,
    string Model = "",
    string ReasoningEffort = "");

public sealed record CodexAgentMessageDeltaEventArgs(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta);

public sealed record CodexTurnCompletedEventArgs(
    string ThreadId,
    string TurnId,
    string Status,
    string? ErrorMessage);

public sealed record CodexItemEventArgs(
    string ThreadId,
    string TurnId,
    string ItemId,
    string ItemType,
    string? Text,
    string? Path,
    string? Status);
