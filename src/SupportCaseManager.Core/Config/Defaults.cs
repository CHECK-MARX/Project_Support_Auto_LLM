using System.Collections.Generic;

namespace SupportCaseManager.Core.Config;

public static class Defaults
{
    public const int MaxRecentCases = 20;

    public static IReadOnlyList<string> DefaultStatuses { get; } = new List<string>
    {
        "受付",
        "調査中",
        "完了",
        "クローズ予定",
        "クローズ",
    };
}
