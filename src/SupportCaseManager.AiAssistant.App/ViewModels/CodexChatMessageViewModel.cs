namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class CodexChatMessageViewModel : ObservableObject
{
    private string text = string.Empty;
    private bool isStreaming;

    public string Role { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string CreatedAtText => CreatedAt.ToString("HH:mm:ss");
    public string RoleDisplay => Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "ユーザー" : "Codex";

    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

    public bool IsStreaming
    {
        get => isStreaming;
        set => SetProperty(ref isStreaming, value);
    }
}
