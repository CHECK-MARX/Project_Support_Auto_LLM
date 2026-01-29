using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Core.Cases;

public static class CaseNaming
{
    private const string InvalidChars = "<>:\"/\\|?*";
    private static readonly Regex StatusStampRegex = new(@"^(?<body>.*?)[_\-\s](?<stamp>\d{8})$", RegexOptions.Compiled);
    private static readonly Regex LegacyStampRegex = new(@"^(?<body>.*?)(?<stamp>\d{4,8})$", RegexOptions.Compiled);

    public const int SupportPadLength = 8;

    public static string SanitizeComponent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        foreach (var ch in InvalidChars)
        {
            trimmed = trimmed.Replace(ch, '_');
        }

        return trimmed;
    }

    public static string NormalizeStatus(string text)
    {
        return SplitStatusAndStamp(text).Status;
    }

    public static (string Status, string Stamp) SplitStatusAndStamp(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var status = text.Trim();
        var stamp = string.Empty;
        while (true)
        {
            var match = StatusStampRegex.Match(status);
            if (!match.Success)
            {
                break;
            }

            var candidate = match.Groups["body"].Value.TrimEnd('_', '-', ' ');
            stamp = match.Groups["stamp"].Value;
            status = string.IsNullOrEmpty(candidate) ? status : candidate;
            if (!string.IsNullOrEmpty(stamp))
            {
                break;
            }
        }

        return (status, stamp);
    }

    public static (string Status, string Stamp) SplitStatusWithLegacy(string text)
    {
        var (status, stamp) = SplitStatusAndStamp(text);
        if (!string.IsNullOrEmpty(stamp))
        {
            return (status, stamp);
        }

        var match = LegacyStampRegex.Match(status);
        if (match.Success)
        {
            var body = match.Groups["body"].Value.TrimEnd('_', '-', ' ');
            var digits = match.Groups["stamp"].Value;
            return (string.IsNullOrEmpty(body) ? status : body, digits);
        }

        return (status, string.Empty);
    }

    public static string EnsureDateString(string value)
    {
        if (value is null)
        {
            throw new ArgumentException("Date value is required.", nameof(value));
        }

        var text = value.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Date value is required.", nameof(value));
        }

        if (text.Length == 6 && IsDigits(text))
        {
            return $"{text}01";
        }

        if (text.Length == 4 && IsDigits(text))
        {
            return $"{text}0101";
        }

        if (text.Length == 8 && IsDigits(text))
        {
            return text;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        throw new ArgumentException($"Unsupported date string: {text}", nameof(value));
    }

    public static string EnsureDateString(DateTime value)
    {
        return value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    public static string NormalizeSupportNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[text.Length];
        var count = 0;
        foreach (var ch in text.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[count++] = ch;
            }
        }

        var compact = count > 0 ? new string(buffer[..count]) : string.Empty;
        if (string.IsNullOrEmpty(compact))
        {
            return string.Empty;
        }

        if (IsDigits(compact))
        {
            return compact.PadLeft(SupportPadLength, '0');
        }

        return compact.ToUpperInvariant();
    }

    public static string FormatSupportNumber(string text)
    {
        return NormalizeSupportNumber(text);
    }

    public static string BuildFolderName(string createdOn, string company, string supportNumber, string status, string? updatedStamp = null)
    {
        var dateText = EnsureDateString(createdOn);
        var safeCompany = SanitizeComponent(company);
        if (string.IsNullOrEmpty(safeCompany))
        {
            throw new ArgumentException("Company name is required.", nameof(company));
        }

        var safeSupport = SanitizeComponent(FormatSupportNumber(supportNumber));
        var safeStatus = SanitizeComponent(NormalizeStatus(status));
        if (string.IsNullOrEmpty(safeStatus))
        {
            throw new ArgumentException("Status is required.", nameof(status));
        }

        var inner = string.IsNullOrEmpty(safeSupport) ? safeCompany : $"{safeCompany}_{safeSupport}";
        var stamp = SanitizeComponent(updatedStamp ?? DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        var folder = $"{dateText}({inner}){safeStatus}";
        if (!string.IsNullOrEmpty(stamp))
        {
            folder = $"{folder}_{stamp}";
        }

        return folder;
    }

    public static string DefaultNoteName(string baseName, string supportNumber)
    {
        var prefix = SanitizeComponent(baseName);
        if (string.IsNullOrEmpty(prefix))
        {
            prefix = "note";
        }

        var support = SanitizeComponent(FormatSupportNumber(supportNumber));
        return string.IsNullOrEmpty(support) ? $"{prefix}.txt" : $"{prefix}_{support}.txt";
    }

    public static string ToIsoTimestamp(DateTime value)
    {
        var baseText = value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var micros = (value.Ticks % TimeSpan.TicksPerSecond) / 10;
        if (micros == 0)
        {
            return baseText;
        }

        return $"{baseText}.{micros:D6}";
    }

    public static string ToIsoTimestamp(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return ToIsoTimestamp(parsed);
        }

        return ToIsoTimestamp(DateTime.UtcNow);
    }

    private static bool IsDigits(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}
