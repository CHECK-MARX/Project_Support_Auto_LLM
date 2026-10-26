using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Artifacts;

public enum ManufacturerCommunicationIntent
{
    AskManufacturer,
    ReplyToManufacturer,
}

/// <summary>
/// Immutable, upstream-resolved input for a manufacturer mail turn.
/// The mail path must consume this context and must not reclassify case history.
/// </summary>
public sealed record ManufacturerMailCaseContext
{
    public ManufacturerCommunicationIntent CommunicationIntent { get; init; } = ManufacturerCommunicationIntent.AskManufacturer;
    public string SupportId { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string ProductPromptFilePath { get; init; } = string.Empty;
    public string SupportToolSettingsFilePath { get; init; } = string.Empty;
    public string CurrentCustomerDeltaFileName { get; init; } = string.Empty;
    public string CurrentCustomerDeltaSourceType { get; init; } = "NONE";
    public IReadOnlyList<string> CurrentCustomerDeltaContent { get; init; } = [];
    public string CurrentOutboundAttachment { get; init; } = string.Empty;
    public IReadOnlyList<string> CurrentOutboundAttachments { get; init; } = [];
    public IReadOnlyList<string> AttachmentWarnings { get; init; } = [];
    public IReadOnlyList<string> OutboundAttachments => CurrentOutboundAttachments.Count > 0
        ? CurrentOutboundAttachments : string.IsNullOrWhiteSpace(CurrentOutboundAttachment) ? [] : [CurrentOutboundAttachment];
    public bool PreviousManufacturerContact { get; init; }
    public bool PreviousCustomerReply { get; init; }
    public bool CloseRequested { get; init; }
    public bool ManufacturerFollowupAllowed { get; init; }
    public bool CustomerReplyAllowed { get; init; }
    public string ImmediateManufacturerResponse { get; init; } = string.Empty;
    public string ImmediateManufacturerRecipientName { get; init; } = string.Empty;
    public ManufacturerProtectedValueSet ProtectedValues { get; init; } = new();

    public bool HasCurrentDelta => !string.IsNullOrWhiteSpace(CurrentCustomerDeltaFileName)
        || CurrentCustomerDeltaContent.Count > 0;
    public bool HasCurrentAttachment => OutboundAttachments.Count > 0;
    public bool IsFollowUp => ManufacturerFollowupAllowed
        && HasCurrentDelta
        && HasCurrentAttachment;

    public bool CanGenerate => HasCurrentDelta
        && (ManufacturerFollowupAllowed || CustomerReplyAllowed);

    public bool CanReplyToManufacturer => CommunicationIntent == ManufacturerCommunicationIntent.ReplyToManufacturer
        && !string.IsNullOrWhiteSpace(ImmediateManufacturerResponse);

    public bool CanGenerateMail => CommunicationIntent == ManufacturerCommunicationIntent.ReplyToManufacturer
        ? CanReplyToManufacturer
        : CanGenerate;
}

public static class ManufacturerMailContentSanitizer
{
    private static readonly Regex Email = new(
        @"\b[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Extension = new(
        @"(?:内線|extension|ext\.?)\s*[:：]?\s*\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Phone = new(
        @"(?:電話|tel\.?|phone|携帯|mobile|fax)\s*[:：]?\s*[+()\d][\d()\-\s]{5,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FileName = new(
        @"(?<![\p{L}\p{N}_-])[\p{L}\p{N}][\p{L}\p{N}_().-]*\.(?:xlsx|csv|pdf|zip|xml|txt|md|docx|pptx)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AbsolutePath = new(
        @"(?:[A-Za-z]:[\\/]|\\\\|/)[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> SanitizeLines(
        IEnumerable<string> values,
        string? companyName,
        string? customerName,
        string currentOutboundAttachment)
    {
        return SanitizeLines(values, companyName, customerName, [currentOutboundAttachment]);
    }

    public static IReadOnlyList<string> SanitizeLines(
        IEnumerable<string> values, string? companyName, string? customerName, IReadOnlyList<string> attachments)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(static value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => Sanitize(line, companyName, customerName, attachments))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Sanitize(string value, string? companyName, string? customerName, IReadOnlyList<string> attachments)
    {
        var line = value.Trim();
        line = ReplaceExact(line, companyName, "お客様");
        line = ReplaceExact(line, customerName, "お客様担当者");
        line = Email.Replace(line, "[customer contact redacted]");
        line = Extension.Replace(line, "[customer extension redacted]");
        line = Phone.Replace(line, "[customer phone redacted]");
        line = FileName.Replace(line, match => attachments.Contains(match.Value, StringComparer.OrdinalIgnoreCase)
            ? match.Value
            : "[non-current attachment omitted]");
        return AbsolutePath.Replace(line, "[local path omitted]").Trim();
    }

    private static string ReplaceExact(string value, string? original, string replacement) =>
        string.IsNullOrWhiteSpace(original)
            ? value
            : value.Replace(original.Trim(), replacement, StringComparison.OrdinalIgnoreCase);
}
