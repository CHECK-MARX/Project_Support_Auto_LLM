using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Prompts;

public static class PolisherPromptBuilder
{
    public static PromptMessages Build(string deterministicAnswer, int maxPromptChars = 12000)
    {
        var system = "あなたの役割は文章校正です。構造化回答の意味を変更せず自然な日本語へ整えてください。" +
            "新しい技術情報、推測、手順、Command、Option、Version、製品仕様を追加してはいけません。" +
            "Product、Version、Engine Pack、Hotfix、Command、CLI option、API、File path、ErrorCode、" +
            "Bug ID、CVE、CWE、DocumentTitle、Page、Section、URL、Readiness、SupportLevelは変更禁止です。";
        var user = "以下の回答案だけを校正してください。根拠のない内容は追加しないでください。\n\n" + deterministicAnswer;
        if (system.Length + user.Length > maxPromptChars)
        {
            user = user[..Math.Max(0, maxPromptChars - system.Length)];
        }

        return new PromptMessages
        {
            SystemPrompt = system,
            UserPrompt = user,
            Diagnostics = new PromptDiagnostics
            {
                ConfiguredMaxPromptChars = maxPromptChars,
                FinalPromptChars = system.Length + user.Length,
                SystemChars = system.Length,
                UserPromptChars = user.Length,
            },
        };
    }
}
