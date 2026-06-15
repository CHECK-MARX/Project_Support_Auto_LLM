namespace SupportCaseManager.Ai.Core.Safety;

public interface ISafetyRedactionService
{
    string RedactForLog(string input);

    string RedactForCloud(string input);

    string RemoveInternalReferencesFromCustomerReply(string input);

    IReadOnlyList<string> FindCustomerReplyWarnings(string input);
}
