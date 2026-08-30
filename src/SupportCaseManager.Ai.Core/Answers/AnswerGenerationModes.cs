namespace SupportCaseManager.Ai.Core.Answers;

public static class AnswerGenerationModes
{
    public const string DeterministicOnly = "DeterministicOnly";
    public const string DeterministicWithPolishing = "DeterministicWithPolishing";
    public const string PolishingFailed = "PolishingFailed";
    public const string PolishingTimedOut = "PolishingTimedOut";
    public const string PolishingCancelled = "PolishingCancelled";
}
