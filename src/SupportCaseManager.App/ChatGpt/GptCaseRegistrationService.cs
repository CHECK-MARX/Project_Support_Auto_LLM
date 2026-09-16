using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Config;

namespace SupportCaseManager.App.ChatGpt;

public sealed record ProductGptTarget(
    string Key,
    string DisplayName,
    string LaunchUrl);

public sealed class ProductGptTargetResolver
{
    public bool TryResolve(ProductDefinition? product, out ProductGptTarget target, out string error)
    {
        target = new ProductGptTarget(string.Empty, string.Empty, string.Empty);
        if (product is null || string.IsNullOrWhiteSpace(product.DisplayName))
        {
            error = "製品が設定されていません。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(product.GptTargetKey) ||
            string.IsNullOrWhiteSpace(product.GptTargetDisplayName) ||
            !GptConversationUrl.TryValidateTarget(product.GptLaunchUrl, out var launchUrl))
        {
            error = $"{product.DisplayName} のGPT登録先が設定されていません。";
            return false;
        }

        target = new ProductGptTarget(
            product.GptTargetKey.Trim(),
            product.GptTargetDisplayName.Trim(),
            launchUrl);
        error = string.Empty;
        return true;
    }
}

public enum GptConversationCreationStatus
{
    Succeeded,
    SentButUrlUnavailable,
    Failed,
}

public sealed record GptConversationCreationResult(
    GptConversationCreationStatus Status,
    string ConversationUrl,
    string Message);

public interface IGptConversationService
{
    Task<GptConversationCreationResult> CreateConversationAsync(
        ProductGptTarget target,
        string approvedBrief,
        CancellationToken cancellationToken = default);

    Task OpenConversationAsync(string conversationUrl, CancellationToken cancellationToken = default);

    Task SendMessageAsync(
        string conversationUrl,
        string message,
        CancellationToken cancellationToken = default);
}

public interface IGptConversationGateway
{
    Task<GptConversationCreationResult> CreateConversationAsync(
        ProductGptTarget target,
        string approvedBrief,
        CancellationToken cancellationToken);

    Task OpenConversationAsync(string conversationUrl, CancellationToken cancellationToken);

    Task SendMessageAsync(string conversationUrl, string message, CancellationToken cancellationToken);
}

public sealed class GptConversationService : IGptConversationService
{
    private readonly IGptConversationGateway gateway;

    public GptConversationService(IGptConversationGateway? gateway = null)
    {
        this.gateway = gateway ?? new ChatGptBrowserGateway();
    }

    public Task<GptConversationCreationResult> CreateConversationAsync(
        ProductGptTarget target,
        string approvedBrief,
        CancellationToken cancellationToken = default)
    {
        if (!GptConversationUrl.TryValidateTarget(target.LaunchUrl, out _) ||
            string.IsNullOrWhiteSpace(approvedBrief))
        {
            return Task.FromResult(new GptConversationCreationResult(
                GptConversationCreationStatus.Failed,
                string.Empty,
                "GPT登録内容または登録先が不正です。"));
        }

        return gateway.CreateConversationAsync(target, approvedBrief, cancellationToken);
    }

    public Task OpenConversationAsync(string conversationUrl, CancellationToken cancellationToken = default)
    {
        if (!GptConversationUrl.TryValidateConversation(conversationUrl, out var normalized))
        {
            throw new InvalidOperationException("保存済みGPTチャットURLが不正です。");
        }

        return gateway.OpenConversationAsync(normalized, cancellationToken);
    }

    public Task SendMessageAsync(
        string conversationUrl,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (!GptConversationUrl.TryValidateConversation(conversationUrl, out var normalized))
        {
            throw new InvalidOperationException("保存済みGPTチャットURLが不正です。");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("GPTへ送信する内容がありません。");
        }

        return gateway.SendMessageAsync(normalized, message, cancellationToken);
    }
}

public static class GptHandoffPrompt
{
    public const string Text = """
        この案件について、AI回答支援へ引き継ぐための現在の重要情報を整理してください。

        メール文面の単なる言い換え、雑談、試行錯誤、却下された案は除外し、
        案件対応を継続するために必要な情報だけを整理してください。

        以下の形式を厳守してください。

        <<<AI_HANDOFF_V1>>>

        【メーカー担当者】
        現在確認できるメーカー担当者名。
        不明なら「不明」。

        【新たに判明した事項】
        新しく判明・確定した重要事項。

        【現在の未解決事項】
        現在も回答・確認が完了していない事項。

        【解決済みに変更した事項】
        以前は未解決だったが現在は解決済みとなった事項。

        【メーカー最新回答の要旨】
        新しいメーカー回答が存在する場合のみ簡潔に記載。

        【お客様対応上の注意事項】
        今後のお客様への回答・確認で重要な事項。

        【現在の次アクション】
        次に実施すべき対応。

        <<<END_AI_HANDOFF_V1>>>

        案件情報に存在しない事実は推測しないでください。
        過去に解決済みの事項を未解決として復活させないでください。
        Scan ID、Engine Version、ログ値等は現在の問題に必要な場合だけ含めてください。
        個人メールアドレス、電話番号、不要な署名情報は原則含めないでください。
        """;
}

public static class GptConversationUrl
{
    public static bool TryValidateTarget(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryCreateChatGptUri(value, out var uri) ||
            !uri.AbsolutePath.StartsWith("/g/", StringComparison.Ordinal) ||
            uri.AbsolutePath.Contains("/c/", StringComparison.Ordinal))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    public static bool TryValidateConversation(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryCreateChatGptUri(value, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var conversationIndex = Array.FindIndex(segments, segment =>
            string.Equals(segment, "c", StringComparison.Ordinal));
        if (conversationIndex < 0 || conversationIndex + 1 >= segments.Length ||
            string.IsNullOrWhiteSpace(segments[conversationIndex + 1]))
        {
            return false;
        }

        normalized = $"https://chatgpt.com/c/{segments[conversationIndex + 1]}";
        return true;
    }

    private static bool TryCreateChatGptUri(string? value, out Uri uri)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri!)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}

public enum GptRegistrationUpdateStatus
{
    Registered,
    NeedsRelink,
    Blocked,
    Failed,
}

public sealed record GptRegistrationUpdate(
    GptRegistrationUpdateStatus Status,
    GptCaseRegistration? Registration,
    string Message);

public sealed record GptHandoffPromptSendResult(bool Succeeded, string Message);

public sealed class GptCaseRegistrationService
{
    private readonly IGptConversationService conversationService;
    private readonly Func<DateTimeOffset> clock;

    public GptCaseRegistrationService(
        IGptConversationService? conversationService = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.conversationService = conversationService ?? new GptConversationService();
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<GptRegistrationUpdate> RegisterNewAsync(
        CaseRecord caseRecord,
        string product,
        ProductGptTarget target,
        string approvedBrief,
        CancellationToken cancellationToken = default)
    {
        var guard = ValidateNewRegistration(caseRecord, product, target);
        if (guard is not null)
        {
            return guard;
        }

        GptConversationCreationResult result;
        try
        {
            result = await conversationService.CreateConversationAsync(target, approvedBrief, cancellationToken);
        }
        catch (Exception ex)
        {
            return new GptRegistrationUpdate(
                GptRegistrationUpdateStatus.Failed,
                null,
                ex.Message);
        }

        if (result.Status == GptConversationCreationStatus.Succeeded &&
            GptConversationUrl.TryValidateConversation(result.ConversationUrl, out var conversationUrl))
        {
            return new GptRegistrationUpdate(
                GptRegistrationUpdateStatus.Registered,
                CreateRegistration(caseRecord, product, target, conversationUrl, GptRegistrationLinkModes.CreatedByApp),
                "GPT案件チャットを登録しました。");
        }

        if (result.Status is GptConversationCreationStatus.SentButUrlUnavailable
            or GptConversationCreationStatus.Succeeded)
        {
            var pending = CreateRegistration(
                caseRecord,
                product,
                target,
                string.Empty,
                GptRegistrationLinkModes.CreatedByApp);
            pending.RegistrationState = GptRegistrationStates.NeedsRelink;
            return new GptRegistrationUpdate(
                GptRegistrationUpdateStatus.NeedsRelink,
                pending,
                "GPTチャットは作成された可能性がありますが、Conversation URLを保存できませんでした。既存GPTチャットを再紐付けしてください。");
        }

        return new GptRegistrationUpdate(
            GptRegistrationUpdateStatus.Failed,
            null,
            string.IsNullOrWhiteSpace(result.Message) ? "GPT案件チャットを登録できませんでした。" : result.Message);
    }

    public GptRegistrationUpdate LinkExisting(
        CaseRecord caseRecord,
        string product,
        ProductGptTarget target,
        string conversationUrl)
    {
        if (!GptConversationUrl.TryValidateConversation(conversationUrl, out var normalized))
        {
            return new GptRegistrationUpdate(
                GptRegistrationUpdateStatus.Failed,
                null,
                "Conversation URLは https://chatgpt.com/c/... 形式で指定してください。");
        }

        if (HasProductMismatch(caseRecord, product, target))
        {
            return new GptRegistrationUpdate(
                GptRegistrationUpdateStatus.Blocked,
                null,
                "保存済みGPT登録情報と現在案件の製品が一致しません。");
        }

        return new GptRegistrationUpdate(
            GptRegistrationUpdateStatus.Registered,
            CreateRegistration(caseRecord, product, target, normalized, GptRegistrationLinkModes.ExistingChatLinked),
            "既存GPTチャットを紐付けました。");
    }

    public Task OpenAsync(string conversationUrl, CancellationToken cancellationToken = default) =>
        conversationService.OpenConversationAsync(conversationUrl, cancellationToken);

    public async Task<GptHandoffPromptSendResult> SendHandoffPromptAsync(
        CaseRecord caseRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseRecord);
        var registration = caseRecord.GptRegistration;
        if (!registration.IsRegistered ||
            !string.Equals(registration.SupportId, caseRecord.SupportNumber, StringComparison.OrdinalIgnoreCase) ||
            !GptConversationUrl.TryValidateConversation(registration.ConversationUrl, out var conversationUrl))
        {
            return new GptHandoffPromptSendResult(
                false,
                "GPT登録済みのConversation URLを確認できません。");
        }

        try
        {
            await conversationService.SendMessageAsync(
                conversationUrl,
                GptHandoffPrompt.Text,
                cancellationToken);
            return new GptHandoffPromptSendResult(
                true,
                "登録済みGPT案件チャットへ引継ぎ情報の作成を依頼しました。回答をコピーして取り込んでください。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GptHandoffPromptSendResult(false, ex.Message);
        }
    }

    private static GptRegistrationUpdate? ValidateNewRegistration(
        CaseRecord caseRecord,
        string product,
        ProductGptTarget target)
    {
        if (caseRecord.GptRegistration.IsRegistered ||
            string.Equals(caseRecord.GptRegistration.RegistrationState, GptRegistrationStates.NeedsRelink, StringComparison.Ordinal))
        {
            return new GptRegistrationUpdate(
                GptRegistrationUpdateStatus.Blocked,
                null,
                "この案件には既にGPT登録情報があります。");
        }

        if (HasProductMismatch(caseRecord, product, target))
        {
            return new GptRegistrationUpdate(
                GptRegistrationUpdateStatus.Blocked,
                null,
                "保存済みGPT登録情報と現在案件の製品が一致しません。");
        }

        return null;
    }

    private static bool HasProductMismatch(
        CaseRecord caseRecord,
        string product,
        ProductGptTarget target)
    {
        var registration = caseRecord.GptRegistration;
        var hasIdentity = !string.IsNullOrWhiteSpace(registration.SupportId)
            || !string.IsNullOrWhiteSpace(registration.Product)
            || !string.IsNullOrWhiteSpace(registration.TargetGptKey);
        return hasIdentity &&
            (!string.Equals(registration.SupportId, caseRecord.SupportNumber, StringComparison.OrdinalIgnoreCase)
             || !string.Equals(registration.Product, product, StringComparison.OrdinalIgnoreCase)
             || !string.Equals(registration.TargetGptKey, target.Key, StringComparison.OrdinalIgnoreCase));
    }

    private GptCaseRegistration CreateRegistration(
        CaseRecord caseRecord,
        string product,
        ProductGptTarget target,
        string conversationUrl,
        string linkMode) => new()
    {
        SupportId = caseRecord.SupportNumber,
        Product = product,
        TargetGptKey = target.Key,
        TargetGptDisplayName = target.DisplayName,
        ConversationUrl = conversationUrl,
        RegisteredAt = clock().ToString("O"),
        LinkMode = linkMode,
        RegistrationState = GptRegistrationStates.Registered,
    };
}
