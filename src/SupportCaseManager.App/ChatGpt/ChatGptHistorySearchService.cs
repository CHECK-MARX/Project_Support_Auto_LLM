using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using SupportCaseManager.Core.Logging;
using WinForms = System.Windows.Forms;

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
    private readonly IAppLogger logger;

    public ChatGptBrowserGateway(IAppLogger? logger = null)
    {
        this.logger = logger ?? NullLogger.Instance;
    }

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
        await SubmitPromptAsync(
                automationTarget.BrowserWindow,
                automationTarget.WebContentRoot,
                promptInput,
                approvedBrief,
                cancellationToken,
                "ChatGPTの入力欄へ登録内容を設定できませんでした。",
                "ChatGPTの送信操作を確認できませんでした。登録内容は送信していません。",
                expectedTargetUrl: target.LaunchUrl,
                expectedTargetName: target.DisplayName)
            .ConfigureAwait(false);
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
        await SubmitPromptAsync(
                automationTarget.BrowserWindow,
                automationTarget.WebContentRoot,
                promptInput,
                message,
                cancellationToken,
                "ChatGPTの入力欄へ引継ぎ依頼を設定できませんでした。送信していません。",
                "ChatGPTの送信操作を確認できませんでした。送信していません。",
                normalizedUrl)
            .ConfigureAwait(false);
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

            if (IsBrowserChromeEdit(edit) && TryReadValue(edit, out var value) &&
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
            if (IsBrowserChromeEdit(edit) && TryReadValue(edit, out var value) &&
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

    private static bool IsBrowserChromeEdit(AutomationElement edit)
    {
        try
        {
            var current = edit;
            while (current is not null)
            {
                if (current.Current.ControlType == ControlType.Document)
                {
                    return false;
                }

                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        return true;
    }

    private static async Task<AutomationElement?> FindSubmitButtonAsync(
        AutomationElement root,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SearchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (AutomationElement button in root.FindAll(
                         TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                if (IsSubmitButtonCandidate(button))
                {
                    return button;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static bool IsSubmitButtonCandidate(AutomationElement button)
    {
        try
        {
            var current = button.Current;
            if (!current.IsEnabled)
            {
                return false;
            }

            if (string.Equals(current.AutomationId, SubmitButtonAutomationId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var automationId = current.AutomationId ?? string.Empty;
            if (automationId.Contains("send", StringComparison.OrdinalIgnoreCase) ||
                automationId.Contains("submit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var accessibleText = $"{current.Name} {current.HelpText}";
            return accessibleText.Contains("送信", StringComparison.OrdinalIgnoreCase) ||
                   accessibleText.Contains("send", StringComparison.OrdinalIgnoreCase) ||
                   accessibleText.Contains("submit", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task SubmitPromptAsync(
        AutomationElement browserWindow,
        AutomationElement webContentRoot,
        AutomationElement promptInput,
        string message,
        CancellationToken cancellationToken,
        string inputError,
        string submitError,
        string? expectedConversationUrl = null,
        string? expectedTargetUrl = null,
        string? expectedTargetName = null)
    {
        if (!promptInput.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternValue) ||
            valuePatternValue is not ValuePattern valuePattern ||
            valuePattern.Current.IsReadOnly)
        {
            logger.Warning("GPT composer input failed: ValuePattern unavailable or read-only.");
            throw new InvalidOperationException($"{inputError} 入力欄の編集機能を確認できません。送信していません。");
        }

        try
        {
            promptInput.SetFocus();
            valuePattern.SetValue(message);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            logger.Warning($"GPT composer input failed: SetValue ({ex.GetType().Name}).");
            throw new InvalidOperationException($"{inputError} 入力欄への書き込みに失敗しました。送信していません。", ex);
        }

        var verifiedInput = await WaitForVerifiedPromptAsync(browserWindow, message, cancellationToken)
            .ConfigureAwait(false);
        if (verifiedInput is null)
        {
            logger.Warning("GPT composer input failed: read-back mismatch or unavailable after SetValue.");
            throw new InvalidOperationException($"{inputError} 入力内容を確認できません。送信していません。");
        }

        if (!DestinationMatches(browserWindow, expectedConversationUrl, expectedTargetUrl, expectedTargetName))
        {
            logger.Warning("GPT composer send blocked: destination URL or GPT title mismatch.");
            throw new InvalidOperationException("対象GPTまたはConversationを再確認できません。送信していません。");
        }

        if (!await FocusComposerAsync(verifiedInput, cancellationToken).ConfigureAwait(false) ||
            await WaitForVerifiedPromptAsync(browserWindow, message, cancellationToken).ConfigureAwait(false) is null)
        {
            logger.Warning("GPT composer send blocked: focus or content could not be reconfirmed.");
            throw new InvalidOperationException(submitError);
        }

        var currentWebRoot = FindChatGptWebContentRoot(browserWindow);
        var submitButton = currentWebRoot is null
            ? null
            : await FindSubmitButtonAsync(currentWebRoot, cancellationToken).ConfigureAwait(false);
        if (!DestinationMatches(browserWindow, expectedConversationUrl, expectedTargetUrl, expectedTargetName))
        {
            logger.Warning("GPT composer send blocked: destination changed before submit.");
            throw new InvalidOperationException("対象GPTまたはConversationを再確認できません。送信していません。");
        }

        if (submitButton is not null && TryInvoke(submitButton))
        {
            return;
        }

        var currentInput = FindChatGptWebContentRoot(browserWindow) is { } currentRoot
            ? FindPromptInput(currentRoot)
            : null;
        if (currentInput is null ||
            !await FocusComposerAsync(currentInput, cancellationToken).ConfigureAwait(false) ||
            await WaitForVerifiedPromptAsync(browserWindow, message, cancellationToken).ConfigureAwait(false) is null)
        {
            logger.Warning("GPT composer Enter fallback blocked: focus or content verification failed.");
            throw new InvalidOperationException(submitError);
        }

        if (!DestinationMatches(browserWindow, expectedConversationUrl, expectedTargetUrl, expectedTargetName))
        {
            logger.Warning("GPT composer Enter fallback blocked: destination changed.");
            throw new InvalidOperationException("対象GPTまたはConversationを再確認できません。送信していません。");
        }

        WinForms.SendKeys.SendWait("{ENTER}");
    }

    private static async Task<AutomationElement?> WaitForVerifiedPromptAsync(
        AutomationElement browserWindow,
        string expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SearchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var webRoot = FindChatGptWebContentRoot(browserWindow);
                var promptInput = webRoot is null ? null : FindPromptInput(webRoot);
                if (promptInput is not null)
                {
                    var value = TryReadValuePattern(promptInput, out var valueText) ? valueText : null;
                    var text = TryReadTextPattern(promptInput, out var textValue) ? textValue : null;
                    if (ComposerReadBackMatches(expected, value, text))
                    {
                        return promptInput;
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                // The web composer can be replaced between lookup and read-back.
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    internal static bool ComposerReadBackMatches(string expected, string? valueReadBack, string? textReadBack) =>
        Matches(expected, valueReadBack) || Matches(expected, textReadBack);

    private static bool Matches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        static string Normalize(string value) =>
            Regex.Replace(value.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();

        return string.Equals(Normalize(expected), Normalize(actual), StringComparison.Ordinal);
    }

    private static bool TryReadTextPattern(AutomationElement element, out string text)
    {
        text = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
                pattern is TextPattern textPattern)
            {
                text = textPattern.DocumentRange.GetText(-1);
                return true;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    private static bool DestinationMatches(
        AutomationElement browserWindow,
        string? expectedConversationUrl,
        string? expectedTargetUrl,
        string? expectedTargetName)
    {
        if (!string.IsNullOrWhiteSpace(expectedConversationUrl))
        {
            return BrowserWindowHasConversationUrl(browserWindow, expectedConversationUrl);
        }

        if (string.IsNullOrWhiteSpace(expectedTargetUrl))
        {
            return false;
        }

        var webRoot = FindChatGptWebContentRoot(browserWindow);
        if (webRoot is null)
        {
            return false;
        }

        foreach (AutomationElement edit in browserWindow.FindAll(
                     TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            if (IsBrowserChromeEdit(edit) && TryReadValue(edit, out var actualUrl) &&
                TargetIdentityMatches(
                    expectedTargetUrl,
                    actualUrl,
                    expectedTargetName,
                    browserWindow.Current.Name,
                    webRoot.Current.Name))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TargetIdentityMatches(
        string expectedUrl,
        string? actualUrl,
        string? expectedName,
        string? browserTitle,
        string? webTitle) =>
        GptConversationUrl.TryValidateTarget(expectedUrl, out var expected) &&
        GptConversationUrl.TryValidateTarget(actualUrl, out var actual) &&
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(expectedName) &&
        ((browserTitle?.Contains(expectedName, StringComparison.OrdinalIgnoreCase) ?? false) ||
         (webTitle?.Contains(expectedName, StringComparison.OrdinalIgnoreCase) ?? false));

    private static async Task<bool> FocusComposerAsync(
        AutomationElement promptInput,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SearchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                promptInput.SetFocus();
                var focused = AutomationElement.FocusedElement;
                if (focused is not null &&
                    string.Equals(
                        focused.Current.AutomationId,
                        PromptInputAutomationId,
                        StringComparison.OrdinalIgnoreCase) &&
                    focused.Current.ProcessId == promptInput.Current.ProcessId)
                {
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var patternValue) &&
                patternValue is InvokePattern invokePattern)
            {
                invokePattern.Invoke();
                return true;
            }
        }
        catch (ElementNotAvailableException)
        {
            // Fall back to the focused composer when the button provider is unstable.
        }
        catch (InvalidOperationException)
        {
            // Fall back to the focused composer when Invoke is not supported at runtime.
        }

        return false;
    }

    private static bool TryReadValuePattern(AutomationElement element, out string value)
    {
        value = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternValue) &&
                patternValue is ValuePattern valuePattern)
            {
                value = valuePattern.Current.Value;
                return true;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }

        return false;
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
                if (IsBrowserChromeEdit(edit) && TryReadValue(edit, out var value) &&
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
