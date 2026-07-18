using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Search;

internal static class SearchDocumentQuality
{
    private static readonly Regex DenseTableOfContentsEntryRegex = new(
        @"(?:アップロード|認証|ログオフ|作成|設定|解析|使用|同期|出力|接続|切断|ダウンロード)(?:する|した)?\d{1,3}(?=\s*[A-Za-z\p{L}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static double CalculateTableOfContentsPenalty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var prefix = text[..Math.Min(400, text.Length)];
        if (prefix.Contains("目次", StringComparison.OrdinalIgnoreCase))
        {
            return 0.38;
        }

        return DenseTableOfContentsEntryRegex.Matches(text).Count >= 5 ? 0.38 : 0;
    }
}
