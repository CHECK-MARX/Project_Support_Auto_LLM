namespace SupportCaseManager.Ai.Core.Facts;

public static class FactKeys
{
    public const string LatestSastVersion = "LatestSastVersion";
    public const string LatestEnginePackVersion = "LatestEnginePackVersion";
    public const string LatestHotfixVersion = "LatestHotfixVersion";
    public const string CurrentInstalledVersion = "CurrentInstalledVersion";
    public const string UpgradePossibility = "UpgradePossibility";
}

public static class QuestionTypes
{
    public const string LatestVersionQuestion = "LatestVersionQuestion";
    public const string FeatureAvailabilityQuestion = "FeatureAvailabilityQuestion";
    public const string HowToQuestion = "HowToQuestion";
    public const string UpgradePossibilityQuestion = "UpgradePossibilityQuestion";
    public const string GeneralSupportQuestion = "GeneralSupportQuestion";
}

public static class FactStatuses
{
    public const string Confirmed = "Confirmed";
    public const string Candidate = "Candidate";
    public const string Missing = "Missing";
    public const string Conflict = "Conflict";
}

public static class FactConfidences
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
}

public static class AnswerReadiness
{
    public const string AutoAnswerable = "AutoAnswerable";
    public const string NeedsConfirmation = "NeedsConfirmation";
    public const string InsufficientEvidence = "InsufficientEvidence";
}
