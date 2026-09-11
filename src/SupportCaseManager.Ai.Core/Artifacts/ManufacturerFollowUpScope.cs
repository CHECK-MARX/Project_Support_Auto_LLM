using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Core.Artifacts;

public enum ManufacturerDraftMode
{
    Initial,
    FollowUp,
}

public enum ManufacturerCaseStage
{
    Unknown,
    CustomerReplyPending,
    ManufacturerFollowUpPending,
}

public sealed record ManufacturerFollowUpScope
{
    public ManufacturerDraftMode Mode { get; init; }
    public IReadOnlyList<SearchSource> CurrentCustomerDelta { get; init; } = [];
    public IReadOnlyList<SearchSource> PriorManufacturerResponse { get; init; } = [];
    public IReadOnlyList<SearchSource> PriorManufacturerRequest { get; init; } = [];
    public bool PreviousManufacturerContactConfirmed { get; init; }
    public bool PreviousCustomerReplyFound { get; init; }
    public IReadOnlyList<SearchSource> GeneratedArtifacts { get; init; } = [];
    public IReadOnlyList<SearchSource> CaseBackground { get; init; } = [];
    public IReadOnlyList<SearchSource> PromptEvidence { get; init; } = [];
    public IReadOnlyList<string> CurrentOutboundAttachments { get; init; } = [];
    public IReadOnlyList<string> PriorSubmissionAttachmentsExcluded { get; init; } = [];
    public int InternalStateExcludedCount { get; init; }
    public string CurrentCustomerDeltaSourceFileName { get; init; } = string.Empty;
    public string CurrentCustomerDeltaSourceRole { get; init; } = string.Empty;
    public string CurrentCustomerDeltaSupportId { get; init; } = string.Empty;
    public string CurrentCustomerDeltaCaseKey { get; init; } = string.Empty;
    public DateTimeOffset? CurrentCustomerDeltaTimestamp { get; init; }
    public ManufacturerCaseStage CaseStage { get; init; }
    public bool CustomerReplyAllowed => CaseStage != ManufacturerCaseStage.ManufacturerFollowUpPending;
    public bool ManufacturerFollowUpAllowed => CaseStage == ManufacturerCaseStage.ManufacturerFollowUpPending;

    public bool HasPriorManufacturerResponse => PriorManufacturerResponse.Count > 0;
    public string ModeText => Mode == ManufacturerDraftMode.FollowUp ? "FOLLOW_UP" : "INITIAL";

    public IReadOnlyList<string> RequiredProtectedValueTexts => PromptEvidence
        .Select(static source => source.Text)
        .Where(static text => !string.IsNullOrWhiteSpace(text))
        .ToArray();
}

public static class ManufacturerFollowUpScopeResolver
{
    private static readonly string[] ManufacturerMarkers =
    [
        "メーカー連携",
        "manufacturer",
        "correspondence",
        "manufacturer-response",
    ];

    private static readonly string[] AdditionalInquiryMarkers =
    [
        "追加問い合わせ",
        "追加質問",
        "追加確認",
        "additional_inquiry",
        "additional-inquiry",
        "follow-up",
        "followup",
    ];

    private static readonly string[] InternalStateMarkers =
    [
        "translation target",
        "translation_target",
        "selected currentcase evidence",
        "no selected currentcase evidence",
        "artifact generation status",
        "parser status",
        "rag score",
        "llm state",
        "validator status",
    ];

    private static readonly string[] GeneratedArtifactMarkers =
    [
        "generated artifact",
        "generated_artifact",
        "生成済み成果物",
        "translated artifact",
        "translation artifact",
    ];

    public static ManufacturerFollowUpScope Resolve(
        IReadOnlyList<SearchSource> evidence,
        IReadOnlyList<CodexCaseFileInfo> files,
        IReadOnlyList<string> requestedAttachmentNames,
        string outputFileName,
        string currentSupportId = "")
    {
        var fileByName = files
            .Where(static file => !string.IsNullOrWhiteSpace(file.FileName))
            .GroupBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var normalizedEvidence = evidence
            .Where(static source => !string.IsNullOrWhiteSpace(source.Title) || !string.IsNullOrWhiteSpace(source.Text))
            .ToArray();
        var internalState = normalizedEvidence.Where(IsInternalState).ToArray();
        var visibleEvidence = normalizedEvidence.Except(internalState).ToArray();
        var priorResponse = visibleEvidence
            .Where(IsPriorManufacturerResponse)
            .Take(24)
            .Select(source => WithRole(source, "PriorManufacturerResponse"))
            .ToArray();
        var priorResponseKeys = priorResponse.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
        var priorResponseTime = priorResponse
            .Select(source => FindFile(source, fileByName)?.LastModifiedAt)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        var currentDelta = visibleEvidence
            .Where(source => IsCurrentCustomerDelta(source, fileByName, priorResponseTime, currentSupportId))
            .Where(source => !priorResponseKeys.Contains(SourceKey(source)))
            .OrderByDescending(source => IsAdditionalInquiryFile(FindFile(source, fileByName)))
            .ThenByDescending(source => FindFile(source, fileByName)?.LastModifiedAt ?? DateTimeOffset.MinValue)
            .Take(1)
            .Select(source => WithRole(source, "CurrentCustomerDelta"))
            .ToArray();
        var currentDeltaKeys = currentDelta.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
        var currentDeltaFileNames = currentDelta
            .Select(static source => source.Title)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priorManufacturerRequest = visibleEvidence
            .Where(source => !priorResponseKeys.Contains(SourceKey(source))
                && !currentDeltaKeys.Contains(SourceKey(source))
                && IsPriorManufacturerRequest(source))
            .Take(24)
            .Select(source => WithRole(source, "PriorManufacturerRequest"))
            .ToArray();
        var priorManufacturerRequestKeys = priorManufacturerRequest.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
        var generatedArtifacts = visibleEvidence
            .Where(source => !priorResponseKeys.Contains(SourceKey(source))
                && !currentDeltaKeys.Contains(SourceKey(source))
                && !priorManufacturerRequestKeys.Contains(SourceKey(source))
                && IsGeneratedArtifact(source))
            .Take(24)
            .Select(source => WithRole(source, "GeneratedArtifact"))
            .ToArray();
        var generatedArtifactKeys = generatedArtifacts.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
        var priorSubmissionSources = visibleEvidence
            .Where(source => !priorResponseKeys.Contains(SourceKey(source))
                && !currentDeltaKeys.Contains(SourceKey(source))
                && !priorManufacturerRequestKeys.Contains(SourceKey(source))
                && !generatedArtifactKeys.Contains(SourceKey(source))
                && IsPriorSubmissionAttachment(source, fileByName))
            .Select(source => WithRole(source, "PriorSubmissionAttachment"))
            .ToArray();
        var priorSubmissionKeys = priorSubmissionSources.Select(SourceKey).ToHashSet(StringComparer.Ordinal);

        var previousManufacturerContactConfirmed = priorManufacturerRequest.Length > 0
            || files.Any(IsPriorManufacturerContactFile);
        var previousCustomerReplyFound = visibleEvidence.Any(IsPreviousCustomerReply)
            || files.Any(IsPreviousCustomerReplyFile);
        var previousCustomerReplyKeys = visibleEvidence
            .Where(IsPreviousCustomerReply)
            .Select(SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var workflowFollowUp = currentDelta.Length > 0
            && previousManufacturerContactConfirmed
            && previousCustomerReplyFound;

        var mode = priorResponse.Length > 0 || workflowFollowUp
            ? ManufacturerDraftMode.FollowUp
            : ManufacturerDraftMode.Initial;
        var deltaFile = currentDelta.Length == 1 ? FindFile(currentDelta[0], fileByName) : null;
        var stage = workflowFollowUp
            ? ManufacturerCaseStage.ManufacturerFollowUpPending
            : currentDelta.Length > 0
                ? ManufacturerCaseStage.CustomerReplyPending
                : ManufacturerCaseStage.Unknown;

        var priorAttachments = requestedAttachmentNames
            .Concat(priorSubmissionSources.Select(static source => source.Title))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !string.Equals(name, outputFileName, StringComparison.OrdinalIgnoreCase))
            .Where(name => !currentDeltaFileNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var outbound = BuildOutboundAttachments(
            mode,
            outputFileName,
            requestedAttachmentNames,
            currentDeltaFileNames);

        var priorAttachmentNames = priorAttachments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var background = visibleEvidence
            .Where(source => !priorResponseKeys.Contains(SourceKey(source))
                && !currentDeltaKeys.Contains(SourceKey(source))
                && !priorManufacturerRequestKeys.Contains(SourceKey(source))
                && !generatedArtifactKeys.Contains(SourceKey(source))
                && !priorSubmissionKeys.Contains(SourceKey(source))
                && !previousCustomerReplyKeys.Contains(SourceKey(source))
                && !priorAttachmentNames.Contains(source.Title))
            .Take(24)
            .Select(source => WithRole(source, "CaseBackground"))
            .ToArray();

        var promptEvidence = mode == ManufacturerDraftMode.FollowUp
            ? currentDelta
                .Concat(priorResponse)
                .Concat(background)
                .Take(64)
                .ToArray()
            : visibleEvidence
                .Take(64)
                .Select(source => WithRole(source, string.IsNullOrWhiteSpace(source.SourceRole) ? "CaseBackground" : source.SourceRole!))
                .ToArray();

        return new ManufacturerFollowUpScope
        {
            Mode = mode,
            CurrentCustomerDelta = currentDelta,
            PriorManufacturerResponse = priorResponse,
            PriorManufacturerRequest = priorManufacturerRequest,
            PreviousManufacturerContactConfirmed = previousManufacturerContactConfirmed,
            PreviousCustomerReplyFound = previousCustomerReplyFound,
            GeneratedArtifacts = generatedArtifacts,
            CaseBackground = background,
            PromptEvidence = promptEvidence,
            CurrentOutboundAttachments = outbound,
            PriorSubmissionAttachmentsExcluded = requestedAttachmentNames.Except(outbound, StringComparer.OrdinalIgnoreCase).ToArray(),
            InternalStateExcludedCount = internalState.Length,
            CurrentCustomerDeltaSourceFileName = deltaFile?.FileName ?? string.Empty,
            CurrentCustomerDeltaSourceRole = deltaFile is null ? string.Empty : "CustomerInquiry",
            CurrentCustomerDeltaSupportId = currentSupportId?.Trim() ?? string.Empty,
            CurrentCustomerDeltaCaseKey = deltaFile?.RelativePath ?? string.Empty,
            CurrentCustomerDeltaTimestamp = deltaFile?.LastModifiedAt,
            CaseStage = stage,
        };
    }

    private static IReadOnlyList<string> BuildOutboundAttachments(
        ManufacturerDraftMode mode,
        string outputFileName,
        IReadOnlyList<string> requestedAttachmentNames,
        IReadOnlySet<string> currentDeltaFileNames)
    {
        var output = string.IsNullOrWhiteSpace(outputFileName)
            ? []
            : new[] { outputFileName };
        // Selected case files are evidence, never an outbound-attachment selection.
        return output;
    }

    private static bool IsCurrentCustomerDelta(
        SearchSource source,
        IReadOnlyDictionary<string, CodexCaseFileInfo> fileByName,
        DateTimeOffset priorResponseTime,
        string currentSupportId)
    {
        if (HasRole(source, "GeneratedArtifact")
            || HasRole(source, "PriorManufacturerRequest")
            || HasRole(source, "PriorManufacturerResponse")
            || HasRole(source, "PriorSubmissionAttachment"))
        {
            return false;
        }

        var file = FindFile(source, fileByName);
        if (file is null
            || file.Kind != CodexCaseFileKind.CustomerInquiry
            || IsGeneratedTranslationFile(file.FileName)
            || !string.Equals(source.SourceType, "CurrentCase", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(currentSupportId)
            && !string.IsNullOrWhiteSpace(source.SupportNumber)
            && !string.Equals(source.SupportNumber, currentSupportId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Role metadata is accepted only after the source has been bound to a
        // current-case customer inquiry file. Text markers alone are not evidence.
        return HasRole(source, "CurrentCustomerDelta")
            || IsAdditionalInquiryFile(file)
            || (priorResponseTime != DateTimeOffset.MinValue && file.LastModifiedAt > priorResponseTime);
    }

    private static bool IsAdditionalInquiryFile(CodexCaseFileInfo? file) => file is not null
        && AdditionalInquiryMarkers.Any(marker =>
            file.FileName.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsGeneratedTranslationFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var marker = stem.LastIndexOf("_EN", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var suffix = stem[(marker + 3)..];
        return suffix.Length == 0
            || suffix.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .All(static part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static bool IsPriorManufacturerResponse(SearchSource source)
    {
        if (HasRole(source, "PriorManufacturerResponse"))
        {
            return true;
        }

        var value = $"{source.Title} {source.FilePath} {source.Text}";
        var isManufacturerDocument = ManufacturerMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!isManufacturerDocument)
        {
            return false;
        }

        return value.Contains("回答", StringComparison.OrdinalIgnoreCase)
            || value.Contains("response", StringComparison.OrdinalIgnoreCase)
            || value.Contains("reply", StringComparison.OrdinalIgnoreCase)
            || value.Contains("thank you for your question", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ご回答", StringComparison.OrdinalIgnoreCase)
            || value.Contains("From:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("差出人:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("送信者:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPriorManufacturerRequest(SearchSource source)
    {
        if (HasRole(source, "PriorManufacturerRequest"))
        {
            return true;
        }

        var value = $"{source.Title} {source.FilePath} {source.Text}";
        if (!ManufacturerMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || IsPriorManufacturerResponse(source))
        {
            return false;
        }

        return value.Contains("質問", StringComparison.OrdinalIgnoreCase)
            || value.Contains("問い合わせ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("request", StringComparison.OrdinalIgnoreCase)
            || value.Contains("inquiry", StringComparison.OrdinalIgnoreCase)
            || value.Contains("confirmation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreviousCustomerReply(SearchSource source)
    {
        var value = $"{source.Title} {source.FilePath}";
        return value.Contains("お客様への返信案", StringComparison.OrdinalIgnoreCase)
            || value.Contains("customer-reply", StringComparison.OrdinalIgnoreCase)
            || value.Contains("customer_reply", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreviousCustomerReplyFile(CodexCaseFileInfo file) =>
        IsPreviousCustomerReplyPath(file.RelativePath) || IsPreviousCustomerReplyPath(file.FileName);

    private static bool IsPriorManufacturerContactFile(CodexCaseFileInfo file) =>
        (file.RelativePath.Contains("メーカー連携内容", StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.Contains("manufacturer", StringComparison.OrdinalIgnoreCase))
        && Path.GetExtension(file.FileName) is ".txt" or ".md" or ".eml";

    private static bool IsPreviousCustomerReplyPath(string value) =>
        value.Contains("お客様への返信案", StringComparison.OrdinalIgnoreCase)
        || value.Contains("customer-reply", StringComparison.OrdinalIgnoreCase)
        || value.Contains("customer_reply", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedArtifact(SearchSource source) =>
        HasRole(source, "GeneratedArtifact")
        || GeneratedArtifactMarkers.Any(marker =>
            $"{source.Title} {source.FilePath} {source.Text}".Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsPriorSubmissionAttachment(
        SearchSource source,
        IReadOnlyDictionary<string, CodexCaseFileInfo> fileByName)
    {
        if (HasRole(source, "PriorSubmissionAttachment"))
        {
            return true;
        }

        var file = FindFile(source, fileByName);
        var extension = Path.GetExtension(source.Title);
        return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)
            || file?.Kind is CodexCaseFileKind.Document or CodexCaseFileKind.Archive;
    }

    private static bool IsInternalState(SearchSource source)
    {
        if (HasRole(source, "InternalRuntimeState"))
        {
            return true;
        }

        var value = $"{source.Title} {source.FilePath} {source.Text}";
        return InternalStateMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasRole(SearchSource source, string role) =>
        string.Equals(source.SourceRole, role, StringComparison.OrdinalIgnoreCase);

    private static SearchSource WithRole(SearchSource source, string role) =>
        source with { SourceRole = role };

    private static string SourceKey(SearchSource source) =>
        string.IsNullOrWhiteSpace(source.SourceId)
            ? $"{source.Title}\u001f{source.Locator}\u001f{source.Text}"
            : source.SourceId;

    private static CodexCaseFileInfo? FindFile(
        SearchSource source,
        IReadOnlyDictionary<string, CodexCaseFileInfo> fileByName) =>
        fileByName.TryGetValue(source.Title, out var file) ? file : null;
}
