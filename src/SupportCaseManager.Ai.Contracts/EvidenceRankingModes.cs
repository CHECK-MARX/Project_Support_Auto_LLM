namespace SupportCaseManager.Ai.Contracts;

public static class EvidenceRankingModes
{
    public const string Phase15 = "Phase15";
    public const string Phase16 = "Phase16";

    public static string Normalize(string? value) =>
        string.Equals(value, Phase16, StringComparison.OrdinalIgnoreCase)
            ? Phase16
            : Phase15;
}
