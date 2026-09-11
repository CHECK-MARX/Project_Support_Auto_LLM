using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ManufacturerSafeContext
{
    public ManufacturerDraftMode DraftMode { get; init; } = ManufacturerDraftMode.Initial;
    public string ProductName { get; init; } = string.Empty;
    public string ProductVersion { get; init; } = string.Empty;
    public string SupportId { get; init; } = string.Empty;
    public string CurrentCustomerDeltaSourceFileName { get; init; } = string.Empty;
    public ManufacturerCaseStage CaseStage { get; init; }
    public string ManufacturerCaseId { get; init; } = string.Empty;
    public string ManufacturerReferenceId { get; init; } = string.Empty;
    public string RequestedTask { get; init; } = string.Empty;
    public ManufacturerRecipient ManufacturerRecipient { get; init; } = new();
    public IReadOnlyList<string> CurrentCustomerDeltaTechnicalContent { get; init; } = [];
    public IReadOnlyList<string> RelevantPriorManufacturerResponse { get; init; } = [];
    public bool PreviousManufacturerContactConfirmed { get; init; }
    public bool PreviousCustomerReplyFound { get; init; }
    public string PriorTechnicalContext { get; init; } = string.Empty;
    public IReadOnlyList<string> CurrentOutboundAttachments { get; init; } = [];
    public IReadOnlyList<string> CurrentOutboundArtifactTechnicalContent { get; init; } = [];
    public IReadOnlyList<string> RequiredProtectedTechnicalValues { get; init; } = [];
    public ManufacturerProtectedValueSet ProtectedValues { get; init; } = new();
    public string ToyoSenderName { get; init; } = "Ken Ito";
    public string ToyoSenderOrganization { get; init; } = "Toyo Corporation";
    public string ToyoSenderSignature => $"{ToyoSenderName}\n{ToyoSenderOrganization}";
}

public static class ManufacturerSafeContextFactory
{
    private static readonly Regex Email = new(
        @"\b[^\s<>]+@[^\s<>]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Extension = new(
        @"(?:内線|extension|ext\.?|内線番号)\s*[:：]?\s*\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Phone = new(
        @"(?:電話|tel\.?|phone|携帯|mobile|fax)\s*[:：]?\s*[+()\d][\d()\-\s]{5,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FileName = new(
        @"(?<![\p{L}\p{N}_-])[\p{L}\p{N}][\p{L}\p{N}_().-]*\.(?:xlsx|csv|pdf|zip|xml|txt|md|docx|pptx)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AbsolutePath = new(
        @"(?:[A-Za-z]:[\\/]|\\\\|/)[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] InternalMarkers =
    [
        "CurrentCase Evidence",
        "selected evidence",
        "selected CurrentCase",
        "translation target",
        "translation_target",
        "target elements",
        "artifact generation",
        "parser",
        "validator",
        "protected value",
        "Internal Runtime",
        "Source Role",
        "GeneratedArtifact",
        "RAG",
        "LLM",
    ];

    public static ManufacturerSafeContext Create(
        string productName,
        string productVersion,
        string supportId,
        string companyName,
        string customerName,
        ManufacturerRecipient manufacturerRecipient,
        ManufacturerFollowUpScope scope,
        string outputFileName,
        string translationSummary = "",
        string initialInquiryText = "",
        string userInstruction = "",
        string priorTechnicalAnswer = "")
    {
        var outbound = new HashSet<string>(scope.CurrentOutboundAttachments
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileName(value) ?? string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var currentDelta = scope.CurrentCustomerDelta
            .Select(source => Sanitize(source.Text, companyName, customerName, outbound))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scope.Mode == ManufacturerDraftMode.Initial
            && (!ContainsQuestionSignal(currentDelta) || currentDelta.Length == 0))
        {
            var safeInquiry = Sanitize(initialInquiryText, companyName, customerName, outbound);
            if (!string.IsNullOrWhiteSpace(safeInquiry))
            {
                currentDelta = [safeInquiry];
            }
        }
        var priorResponse = scope.PriorManufacturerResponse
            .Select(source => Sanitize(source.Text, companyName, customerName, outbound))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var safePriorTechnicalAnswer = Sanitize(priorTechnicalAnswer, companyName, customerName, outbound);
        var safeTranslationSummary = Sanitize(translationSummary, companyName, customerName, outbound);

        var protectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet(
            productName,
            supportId,
            outputFileName,
            outbound.ToArray(),
            currentDelta
                .Concat(priorResponse)
                .Append(safePriorTechnicalAnswer)
                .Append(safeTranslationSummary)
                .ToArray());

        var context = new ManufacturerSafeContext
        {
            DraftMode = scope.Mode,
            ProductName = productName?.Trim() ?? string.Empty,
            ProductVersion = productVersion?.Trim() ?? string.Empty,
            SupportId = supportId?.Trim() ?? string.Empty,
            CurrentCustomerDeltaSourceFileName = scope.CurrentCustomerDeltaSourceFileName,
            CaseStage = scope.CaseStage,
            RequestedTask = Sanitize(userInstruction, companyName, customerName, outbound),
            ManufacturerRecipient = manufacturerRecipient,
            CurrentCustomerDeltaTechnicalContent = currentDelta,
            RelevantPriorManufacturerResponse = priorResponse,
            PreviousManufacturerContactConfirmed = scope.PreviousManufacturerContactConfirmed,
            PreviousCustomerReplyFound = scope.PreviousCustomerReplyFound,
            PriorTechnicalContext = safePriorTechnicalAnswer,
            CurrentOutboundAttachments = outbound.ToArray(),
            CurrentOutboundArtifactTechnicalContent = SplitLines(safeTranslationSummary),
            RequiredProtectedTechnicalValues = protectedValues.Values,
            ProtectedValues = protectedValues,
        };
        var briefValues = ManufacturerMailBriefBuilder.Build(context).MailBodyRequiredLiterals.ToHashSet(StringComparer.Ordinal);
        var scopedValues = protectedValues with { Items = protectedValues.Items.Where(item => briefValues.Contains(item.Literal)).ToArray() };
        return context with { ProtectedValues = scopedValues, RequiredProtectedTechnicalValues = scopedValues.Values };
    }

    private static bool ContainsQuestionSignal(IEnumerable<string> values) => values.Any(value =>
        Regex.IsMatch(
            value,
            @"質問\s*\d|Question\s*\d|[?？]|ご教示|教えて|確認したい|確認してください|please\s+(?:confirm|explain)|could\s+you",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static string Sanitize(
        string? text,
        string companyName,
        string customerName,
        IReadOnlySet<string> outboundAttachments)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var safeLines = new List<string>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = RemoveInternalMetadata(rawLine.Trim());
            if (string.IsNullOrWhiteSpace(line)
                || line.Contains("署名", StringComparison.OrdinalIgnoreCase)
                || line.Contains("signature", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line = ReplaceExact(line, companyName, "お客様");
            line = ReplaceExact(line, customerName, "お客様担当者");
            line = Email.Replace(line, "[customer contact redacted]");
            line = Extension.Replace(line, "[customer extension redacted]");
            line = Phone.Replace(line, "[customer phone redacted]");
            line = FileName.Replace(line, match =>
                outboundAttachments.Contains(match.Value.Trim())
                    ? match.Value.Trim()
                    : "[non-current attachment omitted]");
            line = AbsolutePath.Replace(line, "[local path omitted]");
            if (!string.IsNullOrWhiteSpace(line))
            {
                safeLines.Add(line);
            }
        }

        return string.Join(Environment.NewLine, safeLines).Trim();
    }

    private static IReadOnlyList<string> SplitLines(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(static line => line.Trim())
        .Where(static line => !string.IsNullOrWhiteSpace(line))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string RemoveInternalMetadata(string value)
    {
        var line = Regex.Replace(
            value,
            @"(?:CurrentCase\s+Evidence|selected(?:\s+CurrentCase)?\s+evidence|translation\s+target(?:\s+elements)?|translation_target|target\s+elements|artifact\s+generation(?:\s+status)?|parser\s+status|validator\s+status|rag\s+score|llm\s+state|GeneratedArtifact|Source\s+Role|protected\s+value)\s*[:：=]\s*[^;|]+",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var marker in InternalMarkers)
        {
            var pattern = marker.Any(static character => !char.IsLetterOrDigit(character))
                ? $"(?<!\\w){Regex.Escape(marker)}(?!\\w)"
                : $"\\b{Regex.Escape(marker)}\\b";
            line = Regex.Replace(
                line,
                pattern,
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return line.Trim();
    }

    private static string ReplaceExact(string value, string? original, string replacement)
    {
        return string.IsNullOrWhiteSpace(original)
            ? value
            : value.Replace(original.Trim(), replacement, StringComparison.OrdinalIgnoreCase);
    }
}
