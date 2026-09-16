using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace SupportCaseManager.App.ChatGpt;

public enum ChatGptHistorySearchStatus
{
    Succeeded,
    Skipped,
    Failed,
}

public sealed record ChatGptHistorySearchResult(
    ChatGptHistorySearchStatus Status,
    string Message)
{
    public static ChatGptHistorySearchResult Skipped() =>
        new(ChatGptHistorySearchStatus.Skipped, string.Empty);

    public static ChatGptHistorySearchResult Success() =>
        new(ChatGptHistorySearchStatus.Succeeded, "ChatGPTの履歴検索を表示しました。");

    public static ChatGptHistorySearchResult Failure() =>
        new(ChatGptHistorySearchStatus.Failed, "ChatGPTの履歴検索を開始できませんでした。");
}

public interface IChatGptHistorySearchService
{
    Task<ChatGptHistorySearchResult> SearchExistingConversationAsync(
        string? supportId,
        CancellationToken cancellationToken = default);
}

public interface IChatGptBrowserGateway
{
    Task OpenHistorySearchAsync(string supportId, CancellationToken cancellationToken);
}

public sealed class ChatGptHistorySearchService : IChatGptHistorySearchService
{
    private readonly IChatGptBrowserGateway browserGateway;

    public ChatGptHistorySearchService(IChatGptBrowserGateway? browserGateway = null)
    {
        this.browserGateway = browserGateway ?? new ChatGptBrowserGateway();
    }

    public async Task<ChatGptHistorySearchResult> SearchExistingConversationAsync(
        string? supportId,
        CancellationToken cancellationToken = default)
    {
        var normalizedSupportId = supportId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSupportId))
        {
            return ChatGptHistorySearchResult.Skipped();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await browserGateway.OpenHistorySearchAsync(normalizedSupportId, cancellationToken)
                .ConfigureAwait(true);
            return ChatGptHistorySearchResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ChatGptHistorySearchResult.Failure();
        }
        catch
        {
            return ChatGptHistorySearchResult.Failure();
        }
    }
}

public sealed class ChatGptBrowserGateway : IChatGptBrowserGateway, IGptConversationGateway
{
    public const string ChatGptUrl = "https://chatgpt.com/";
    internal const string SearchInputAutomationId = "global-search-modal-input";
    internal const string PromptInputAutomationId = "prompt-textarea";
    internal const string SubmitButtonAutomationId = "composer-submit-button";
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConversationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly string[] BrowserProcessNames = ["msedge", "chrome"];
    private static readonly string[] SearchButtonNames = ["検索", "Search", "Search chats", "チャットを検索"];

    public async Task OpenHistorySearchAsync(string supportId, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = ChatGptUrl,
            UseShellExecute = true,
        });

        var target = await FindChatGptTargetAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ChatGPTの画面を特定できませんでした。");
        BringToForeground(target.BrowserWindow);

        var searchButton = FindSearchButton(target.WebContentRoot)
            ?? throw new InvalidOperationException("ChatGPTの履歴検索ボタンを特定できませんでした。");
        if (!searchButton.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePatternValue) ||
            invokePatternValue is not InvokePattern invokePattern)
        {
            throw new InvalidOperationException("ChatGPTの履歴検索ボタンを操作できませんでした。");
        }

        invokePattern.Invoke();

        var searchInput = await FindSearchInputAsync(target.WebContentRoot, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ChatGPTの履歴検索欄を特定できませんでした。");
        if (!searchInput.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternValue) ||
            valuePatternValue is not ValuePattern valuePattern ||
            valuePattern.Current.IsReadOnly)
        {
            throw new InvalidOperationException("ChatGPTの履歴検索欄へ入力できませんでした。");
        }

        searchInput.SetFocus();
        valuePattern.SetValue(supportId);
    }

    public async Task<GptConversationCreationResult> CreateConversationAsync(
        ProductGptTarget target,
        string approvedBrief,
        CancellationToken cancellationToken)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target.LaunchUrl,
            UseShellExecute = true,
        });

        var automationTarget = await FindChatGptTargetAsync(
                cancellationToken,
                target.DisplayName,
                target.LaunchUrl,
                requireSearchButton: false)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("対象のCustom GPT画面を特定できませんでした。");
        BringToForeground(automationTarget.BrowserWindow);

        var promptInput = FindPromptInput(automationTarget.WebContentRoot)
            ?? throw new InvalidOperationException("ChatGPTの入力欄を特定できませんでした。");
        if (!promptInput.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternValue) ||
            valuePatternValue is not ValuePattern valuePattern ||
            valuePattern.Current.IsReadOnly)
        {
            throw new InvalidOperationException("ChatGPTの入力欄へ登録内容を設定できませんでした。");
        }

        promptInput.SetFocus();
        valuePattern.SetValue(approvedBrief);
        var submitButton = await FindSubmitButtonAsync(
                automationTarget.WebContentRoot,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("ChatGPTの送信ボタンを特定できませんでした。");
        if (!submitButton.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePatternValue) ||
            invokePatternValue is not InvokePattern invokePattern)
        {
            throw new InvalidOperationException("ChatGPTの送信ボタンを操作できませんでした。");
        }

        invokePattern.Invoke();
        string? conversationUrl;
        try
        {
            conversationUrl = await FindConversationUrlAsync(
                    automationTarget.BrowserWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            conversationUrl = null;
        }
        return string.IsNullOrWhiteSpace(conversationUrl)
            ? new GptConversationCreationResult(
                GptConversationCreationStatus.SentButUrlUnavailable,
                string.Empty,
                "初回メッセージ送信後にConversation URLを取得できませんでした。")
            : new GptConversationCreationResult(
                GptConversationCreationStatus.Succeeded,
                conversationUrl,
                string.Empty);
    }

    public Task OpenConversationAsync(string conversationUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo
        {
            FileName = conversationUrl,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    public async Task SendMessageAsync(
        string conversationUrl,
        string message,
        CancellationToken cancellationToken)
    {
        if (!GptConversationUrl.TryValidateConversation(conversationUrl, out var normalizedUrl))
        {
            throw new InvalidOperationException("保存済みGPTチャットURLが不正です。");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = normalizedUrl,
            UseShellExecute = true,
        });

        var automationTarget = await FindChatGptTargetAsync(
                cancellationToken,
                expectedConversationUrl: normalizedUrl,
                requireSearchButton: false,
                timeout: ConversationTimeout)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("登録済みGPT案件チャットを特定できませんでした。送信していません。");
        BringToForeground(automationTarget.BrowserWindow);

        var promptInput = FindPromptInput(automationTarget.WebContentRoot)
            ?? throw new InvalidOperationException("ChatGPTの入力欄を特定できませんでした。送信していません。");
        if (!promptInput.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternValue) ||
            valuePatternValue is not ValuePattern valuePattern ||
            valuePattern.Current.IsReadOnly)
        {
            throw new InvalidOperationException("ChatGPTの入力欄へ引継ぎ依頼を設定できませんでした。送信していません。");
        }

        promptInput.SetFocus();
        valuePattern.SetValue(message);
        var submitButton = await FindSubmitButtonAsync(
                automationTarget.WebContentRoot,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("ChatGPTの送信ボタンを特定できませんでした。送信していません。");
        if (!submitButton.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePatternValue) ||
            invokePatternValue is not InvokePattern invokePattern)
        {
            throw new InvalidOperationException("ChatGPTの送信ボタンを操作できませんでした。送信していません。");
        }

        invokePattern.Invoke();
    }

    private static async Task<ChatGptAutomationTarget?> FindChatGptTargetAsync(
        CancellationToken cancellationToken,
        string? expectedTitle = null,
        string? expectedTargetUrl = null,
        bool requireSearchButton = true,
        string? expectedConversationUrl = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? SearchTimeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            foreach (AutomationElement window in windows)
            {
                if (!IsSupportedBrowser(window))
                {
                    continue;
                }

                var webContentRoot = FindChatGptWebContentRoot(window);
                if (webContentRoot is not null &&
                    (!requireSearchButton || FindSearchButton(webContentRoot) is not null) &&
                    (string.IsNullOrWhiteSpace(expectedTargetUrl) ||
                     BrowserWindowHasTargetUrl(window, expectedTargetUrl)) &&
                    (string.IsNullOrWhiteSpace(expectedConversationUrl) ||
                     BrowserWindowHasConversationUrl(window, expectedConversationUrl)) &&
                    (string.IsNullOrWhiteSpace(expectedTitle) ||
                     window.Current.Name.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase) ||
                     webContentRoot.Current.Name.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    return new ChatGptAutomationTarget(window, webContentRoot);
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static bool BrowserWindowHasConversationUrl(
        AutomationElement browserWindow,
        string expectedConversationUrl)
    {
        if (!GptConversationUrl.TryValidateConversation(expectedConversationUrl, out var expected))
        {
            return false;
        }

        foreach (AutomationElement edit in browserWindow.FindAll(
                     TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            if (string.Equals(
                    edit.Current.AutomationId,
                    PromptInputAutomationId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadValue(edit, out var value) &&
                GptConversationUrl.TryValidateConversation(value, out var actual) &&
                string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BrowserWindowHasTargetUrl(AutomationElement browserWindow, string expectedTargetUrl)
    {
        if (!GptConversationUrl.TryValidateTarget(expectedTargetUrl, out var expected))
        {
            return false;
        }

        foreach (AutomationElement edit in browserWindow.FindAll(
                     TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            if (TryReadValue(edit, out var value) &&
                GptConversationUrl.TryValidateTarget(value, out var actual) &&
                string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AutomationElement? FindSearchButton(AutomationElement browserWindow)
    {
        var nameConditions = SearchButtonNames
            .Select(name => (Condition)new PropertyCondition(AutomationElement.NameProperty, name))
            .ToArray();
        return browserWindow.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new OrCondition(nameConditions)));
    }

    private static async Task<AutomationElement?> FindSearchInputAsync(
        AutomationElement browserWindow,
        CancellationToken cancellationToken)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.AutomationIdProperty, SearchInputAutomationId));
        var deadline = DateTime.UtcNow + SearchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = browserWindow.FindFirst(TreeScope.Descendants, condition);
            if (input is not null)
            {
                return input;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static AutomationElement? FindChatGptWebContentRoot(AutomationElement browserWindow)
    {
        var promptInput = FindPromptInput(browserWindow);
        if (promptInput is null)
        {
            return null;
        }

        var current = promptInput;
        while (current is not null && current != browserWindow)
        {
            if (current.Current.ControlType == ControlType.Document)
            {
                return current;
            }

            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return null;
    }

    private static AutomationElement? FindPromptInput(AutomationElement root) => root.FindFirst(
        TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, PromptInputAutomationId));

    private static async Task<AutomationElement?> FindSubmitButtonAsync(
        AutomationElement root,
        CancellationToken cancellationToken)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.AutomationIdProperty, SubmitButtonAutomationId));
        var deadline = DateTime.UtcNow + SearchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var button = root.FindFirst(TreeScope.Descendants, condition);
            if (button is not null)
            {
                return button;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<string?> FindConversationUrlAsync(
        AutomationElement browserWindow,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ConversationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (AutomationElement edit in browserWindow.FindAll(
                         TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
            {
                if (TryReadValue(edit, out var value) &&
                    GptConversationUrl.TryValidateConversation(value, out var normalized))
                {
                    return normalized;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static bool TryReadValue(AutomationElement element, out string value)
    {
        value = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternValue) &&
                valuePatternValue is ValuePattern valuePattern)
            {
                value = valuePattern.Current.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            value = element.Current.HelpText;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            value = element.Current.Name;
            if (value.StartsWith("https://chatgpt.com/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        return false;
    }

    private static bool IsSupportedBrowser(AutomationElement window)
    {
        try
        {
            using var process = Process.GetProcessById(window.Current.ProcessId);
            return BrowserProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void BringToForeground(AutomationElement browserWindow)
    {
        var windowHandle = new IntPtr(browserWindow.Current.NativeWindowHandle);
        if (windowHandle == IntPtr.Zero || !SetForegroundWindow(windowHandle))
        {
            throw new InvalidOperationException("ChatGPTのブラウザ画面を前面表示できませんでした。");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private sealed record ChatGptAutomationTarget(
        AutomationElement BrowserWindow,
        AutomationElement WebContentRoot);
}
