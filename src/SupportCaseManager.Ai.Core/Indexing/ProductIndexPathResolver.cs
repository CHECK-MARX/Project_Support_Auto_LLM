using System.Security.Cryptography;
using System.Text;

namespace SupportCaseManager.Ai.Core.Indexing;

public static class ProductIndexPathResolver
{
    private const int MaxSafeNameLength = 80;

    public static string GetProductIndexFolder(string aiIndexFolder, string productName)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder))
        {
            throw new ArgumentException("AI index folder is required.", nameof(aiIndexFolder));
        }

        return Path.Combine(aiIndexFolder, "products", ToSafeProductFolderName(productName));
    }

    public static string ToSafeProductFolderName(string? productName)
    {
        var raw = string.IsNullOrWhiteSpace(productName) ? "Default" : productName.Trim();
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(raw.Length);
        var changed = false;

        foreach (var ch in raw)
        {
            if (invalidChars.Contains(ch) || char.IsControl(ch) || ch is '/' or '\\' or ':')
            {
                builder.Append('_');
                changed = true;
            }
            else
            {
                builder.Append(ch);
            }
        }

        var safe = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "Product";
            changed = true;
        }

        if (safe.Length > MaxSafeNameLength)
        {
            safe = safe[..MaxSafeNameLength].Trim(' ', '.');
            changed = true;
        }

        return changed ? $"{safe}_{ShortHash(raw)}" : safe;
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
