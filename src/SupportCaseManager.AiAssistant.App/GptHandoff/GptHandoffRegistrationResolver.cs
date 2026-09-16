using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.App.ChatGpt;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.AiAssistant.App.GptHandoff;

internal static class GptHandoffRegistrationResolver
{
    public static bool IsRegisteredForCurrentCase(
        GptHandoffContext context,
        string supportNumber,
        string productName) =>
        TryResolve(context, supportNumber, productName, out _, out _);

    public static bool TryResolve(
        GptHandoffContext context,
        string supportNumber,
        string productName,
        out GptCaseRegistration registration,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(context);
        registration = new GptCaseRegistration();
        error = string.Empty;

        if (!string.Equals(
                context.RegistrationState,
                GptRegistrationStates.Registered,
                StringComparison.Ordinal))
        {
            error = "現在案件のGPT登録状態を確認できません。";
            return false;
        }

        var contextSupport = CaseNaming.NormalizeSupportNumber(context.SupportId);
        var currentSupport = CaseNaming.NormalizeSupportNumber(supportNumber);
        if (string.IsNullOrWhiteSpace(contextSupport) ||
            !string.Equals(contextSupport, currentSupport, StringComparison.OrdinalIgnoreCase))
        {
            error = "GPT登録情報と現在案件のSupport IDが一致しません。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.Product) ||
            string.IsNullOrWhiteSpace(productName) ||
            !string.Equals(context.Product.Trim(), productName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = "GPT登録情報と現在案件の製品が一致しません。";
            return false;
        }

        if (!GptConversationUrl.TryValidateConversation(context.ConversationUrl, out var conversationUrl))
        {
            error = "登録済みGPT案件チャットのConversation URLを確認できません。";
            return false;
        }

        registration = new GptCaseRegistration
        {
            SupportId = contextSupport,
            Product = context.Product.Trim(),
            TargetGptKey = context.TargetGptKey,
            TargetGptDisplayName = context.TargetGptDisplayName,
            ConversationUrl = conversationUrl,
            RegisteredAt = context.RegisteredAt,
            LinkMode = context.LinkMode,
            RegistrationState = context.RegistrationState,
            LastImportedHash = context.LastImportedHash,
            LastImportedAt = context.LastImportedAt,
            ImportVersion = context.ImportVersion,
        };
        return true;
    }
}
