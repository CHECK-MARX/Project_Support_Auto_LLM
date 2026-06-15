using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Search;

public static class FreshnessEvidenceAutoSelector
{
    public const double OfficialDocAutoSelectMinimumScore = 0.25;

    public static bool ShouldAutoSelect(
        SearchSource source,
        bool isFreshnessSensitive,
        double autoSelectMinimumScore)
    {
        var score = source.Score ?? 0;
        if (string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
        {
            if (isFreshnessSensitive)
            {
                return score >= OfficialDocAutoSelectMinimumScore;
            }

            return score >= autoSelectMinimumScore;
        }

        return score >= autoSelectMinimumScore;
    }

    public static int GetSourcePriority(string? sourceType, bool freshnessSensitive)
    {
        if (freshnessSensitive)
        {
            return sourceType switch
            {
                "OfficialDoc" => 0,
                "Manual" => 1,
                "PastCaseNote" => 2,
                _ => 3,
            };
        }

        return sourceType switch
        {
            "Manual" => 0,
            "OfficialDoc" => 1,
            "PastCaseNote" => 2,
            _ => 3,
        };
    }
}
