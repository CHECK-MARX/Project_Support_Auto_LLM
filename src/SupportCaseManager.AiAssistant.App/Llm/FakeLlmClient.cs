using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.AiAssistant.App.Llm;

public sealed class FakeLlmClient : ILlmClient
{
    public Task<LlmGenerationResult> GenerateAsync(
        PromptMessages messages,
        LlmProviderSettings settings,
        bool disableThinking = true,
        CancellationToken cancellationToken = default)
    {
        const string response = """
            {
              "customerReplyDraft": "これはモック回答案です。実LLM接続はまだ行っていません。",
              "internalMemo": "モックLLMにより生成された社内メモです。",
              "needConfirmations": [
                {
                  "question": "製品バージョンを確認してください。",
                  "reason": "問い合わせ本文からバージョン情報が確認できません。",
                  "priority": "Normal",
                  "relatedSourceIds": []
                }
              ],
              "evidence": [],
              "confidence": 0.5,
              "warnings": ["これはモック応答です。"]
            }
            """;

        return Task.FromResult(new LlmGenerationResult
        {
            Content = response,
            DoneReason = "stop",
            ThinkDisabledSent = disableThinking,
        });
    }
}
