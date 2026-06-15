namespace SupportCaseManager.Ai.Core.Llm;

public static class ModelRecommendationHelper
{
    public static string BuildRecommendationText(string? modelName)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"現在モデル: {ValueOrUnset(modelName)}");
        builder.AppendLine("推奨確認順:");
        builder.AppendLine("1. qwen3:8b または qwen3:14b で think:false の短文Chat testを確認");
        builder.AppendLine("2. OfficialDocが回答根拠に使われるか確認");
        builder.AppendLine("3. qwen3:8b と qwen3:14b で回答品質を比較");

        if (IsQwen314B(modelName))
        {
            builder.AppendLine();
            builder.AppendLine(BuildQwen314BSettings());
        }
        else if (IsQwen38B(modelName))
        {
            builder.AppendLine();
            builder.AppendLine(BuildQwen38BSettings());
        }

        return builder.ToString();
    }

    public static string BuildQwen314BSettings()
    {
        return """
            qwen3:14b 推奨初期設定:
            最大根拠件数: 2
            最大プロンプト文字数: 6000
            最大出力トークン: 600
            タイムアウト秒数: 600
            Temperature: 0.2
            Thinkingを無効化: ON
            """;
    }

    public static string BuildQwen38BSettings()
    {
        return """
            qwen3:8b 推奨初期設定:
            最大根拠件数: 2〜3
            最大プロンプト文字数: 6000〜8000
            最大出力トークン: 600〜800
            タイムアウト秒数: 300〜600
            Temperature: 0.2
            Thinkingを無効化: ON
            """;
    }

    private static bool IsQwen38B(string? modelName)
    {
        return NormalizeModel(modelName).StartsWith("qwen3:8b", StringComparison.OrdinalIgnoreCase)
            || NormalizeModel(modelName).Equals("qwen3", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQwen314B(string? modelName)
    {
        return NormalizeModel(modelName).StartsWith("qwen3:14b", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModel(string? modelName)
    {
        return string.IsNullOrWhiteSpace(modelName) ? string.Empty : modelName.Trim();
    }

    private static string ValueOrUnset(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(未設定)" : value.Trim();
    }
}
