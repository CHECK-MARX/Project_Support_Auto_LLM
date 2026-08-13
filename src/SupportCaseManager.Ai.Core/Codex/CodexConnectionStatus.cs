namespace SupportCaseManager.Ai.Core.Codex;

public enum CodexConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    StartingThread,
    Investigating,
    GeneratingAnswer,
    Interrupting,
    Completed,
    ReconnectRequired,
    AuthenticationRequired,
    Error,
}

public static class CodexConnectionStateLabels
{
    public static string ToJapanese(this CodexConnectionState state)
    {
        return state switch
        {
            CodexConnectionState.Disconnected => "未接続",
            CodexConnectionState.Connecting => "接続中",
            CodexConnectionState.Connected => "接続済み",
            CodexConnectionState.StartingThread => "Thread開始中",
            CodexConnectionState.Investigating => "調査中",
            CodexConnectionState.GeneratingAnswer => "回答生成中",
            CodexConnectionState.Interrupting => "中止処理中",
            CodexConnectionState.Completed => "完了",
            CodexConnectionState.ReconnectRequired => "再接続が必要",
            CodexConnectionState.AuthenticationRequired => "認証が必要",
            CodexConnectionState.Error => "エラー",
            _ => "不明",
        };
    }
}
