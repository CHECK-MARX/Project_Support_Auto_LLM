using System.Globalization;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ManufacturerRecipient
{
    public string DisplayName { get; init; } = string.Empty;
    public string EmailAddress { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public DateTimeOffset? SourceTimestamp { get; init; }
    public string ResolutionStatus { get; init; } = "UNRESOLVED";

    public bool IsResolved => string.Equals(ResolutionStatus, "RESOLVED", StringComparison.Ordinal);

    public string DisplayText => IsResolved
        ? string.IsNullOrWhiteSpace(EmailAddress)
            ? DisplayName
            : $"{DisplayName} <{EmailAddress}>"
        : "メーカー担当者を特定できませんでした";
}

public sealed class ManufacturerRecipientResolver
{
    private const int MaximumHeaderCharacters = 256 * 1024;

    private static readonly string[] ManufacturerPathMarkers =
    [
        "メーカー連携内容",
        "manufacturer",
        "correspondence",
    ];

    private static readonly Regex FromHeader = new(
        @"^(?:from|差出人|送信者)\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DateHeader = new(
        @"^(?:date|日時|送信日時|受信日時)\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Address = new(
        @"^(?<name>.*?)\s*<(?<email>[^<>\s]+@[^<>\s]+)>$|^(?<emailOnly>[^<>\s]+@[^<>\s]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ManufacturerRecipient Resolve(
        string caseFolder,
        string supportId,
        string companyName,
        string customerName,
        IEnumerable<CodexCaseFileInfo> files)
    {
        if (!CodexPathPolicy.TryNormalizeRoot(caseFolder, out var root, out _))
        {
            return Unresolved();
        }

        var candidates = files
            .Where(static file => file.CanSendToCodex && IsTextFile(file.FullPath))
            .Where(file => IsManufacturerCorrespondence(root, file))
            .Select(file => TryReadCandidate(root, file, supportId, companyName, customerName, requireResponse: false))
            .Where(static candidate => candidate is not null)
            .Cast<RecipientCandidate>()
            .OrderByDescending(static candidate => candidate.Timestamp)
            .ThenBy(static candidate => candidate.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        return selected is null
            ? Unresolved()
            : new ManufacturerRecipient
            {
                DisplayName = selected.DisplayName,
                EmailAddress = selected.EmailAddress,
                SourceFileName = selected.File.FileName,
                SourceTimestamp = selected.Timestamp,
                ResolutionStatus = "RESOLVED",
            };
    }

    public ManufacturerRecipient ResolveFromPriorResponses(
        IEnumerable<SearchSource> priorResponses,
        string companyName,
        string customerName)
    {
        var candidates = priorResponses
            .Where(static source => !string.IsNullOrWhiteSpace(source.Text))
            .Where(IsManufacturerResponseSource)
            .Select(source => TryReadSourceCandidate(source, companyName, customerName))
            .Where(static candidate => candidate is not null)
            .Cast<SourceRecipientCandidate>()
            .OrderByDescending(static candidate => candidate.Timestamp)
            .ThenBy(static candidate => candidate.SourceFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        return selected is null
            ? Unresolved()
            : new ManufacturerRecipient
            {
                DisplayName = selected.DisplayName,
                EmailAddress = selected.EmailAddress,
                SourceFileName = selected.SourceFileName,
                SourceTimestamp = selected.Timestamp,
                ResolutionStatus = "RESOLVED",
            };
    }

    public ManufacturerRecipient ResolveFromPriorResponseFiles(
        string caseFolder,
        string supportId,
        string companyName,
        string customerName,
        IEnumerable<CodexCaseFileInfo> files)
    {
        if (!CodexPathPolicy.TryNormalizeRoot(caseFolder, out var root, out _))
        {
            return Unresolved();
        }

        var candidates = files
            .Where(static file => file.CanSendToCodex && IsTextFile(file.FullPath))
            .Where(file => IsManufacturerCorrespondence(root, file))
            .Select(file => TryReadCandidate(root, file, supportId, companyName, customerName, requireResponse: true))
            .Where(static candidate => candidate is not null)
            .Cast<RecipientCandidate>()
            .OrderByDescending(static candidate => candidate.Timestamp)
            .ThenBy(static candidate => candidate.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        return selected is null
            ? Unresolved()
            : new ManufacturerRecipient
            {
                DisplayName = selected.DisplayName,
                EmailAddress = selected.EmailAddress,
                SourceFileName = selected.File.FileName,
                SourceTimestamp = selected.Timestamp,
                ResolutionStatus = "RESOLVED",
            };
    }

    private static RecipientCandidate? TryReadCandidate(
        string root,
        CodexCaseFileInfo file,
        string supportId,
        string companyName,
        string customerName,
        bool requireResponse)
    {
        if (!CodexPathPolicy.TryNormalizeFileWithinRoot(root, file.FullPath, out _, out _)
            || file.Size > MaximumHeaderCharacters * 4L)
        {
            return null;
        }

        string text;
        try
        {
            text = ReadHeader(file.FullPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (requireResponse && !LooksLikeManufacturerResponse(text))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(supportId)
            && !ContainsCaseValue(file, text, supportId))
        {
            return null;
        }

        string? fromValue = null;
        DateTimeOffset? headerTimestamp = null;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                continue;
            }

            if (fromValue is null && FromHeader.Match(line) is { Success: true } fromMatch)
            {
                fromValue = fromMatch.Groups["value"].Value.Trim();
            }

            if (!headerTimestamp.HasValue
                && DateHeader.Match(line) is { Success: true } dateMatch
                && DateTimeOffset.TryParse(
                    dateMatch.Groups["value"].Value.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsedTimestamp))
            {
                headerTimestamp = parsedTimestamp;
            }

            if (fromValue is not null && headerTimestamp.HasValue)
            {
                break;
            }
        }

        if (!TryParseSender(fromValue, out var displayName, out var emailAddress)
            || IsExcludedSender(displayName, emailAddress, companyName, customerName))
        {
            return null;
        }

        return new RecipientCandidate(
            file,
            displayName,
            emailAddress,
            headerTimestamp ?? file.LastModifiedAt);
    }

    private static SourceRecipientCandidate? TryReadSourceCandidate(
        SearchSource source,
        string companyName,
        string customerName)
    {
        string? fromValue = null;
        DateTimeOffset? headerTimestamp = null;
        foreach (var rawLine in source.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                continue;
            }

            if (fromValue is null && FromHeader.Match(line) is { Success: true } fromMatch)
            {
                fromValue = fromMatch.Groups["value"].Value.Trim();
            }

            if (!headerTimestamp.HasValue
                && DateHeader.Match(line) is { Success: true } dateMatch
                && DateTimeOffset.TryParse(
                    dateMatch.Groups["value"].Value.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsedTimestamp))
            {
                headerTimestamp = parsedTimestamp;
            }

            if (fromValue is not null && headerTimestamp.HasValue)
            {
                break;
            }
        }

        if (!TryParseSender(fromValue, out var displayName, out var emailAddress)
            || IsExcludedSender(displayName, emailAddress, companyName, customerName))
        {
            return null;
        }

        return new SourceRecipientCandidate(
            source.Title,
            displayName,
            emailAddress,
            headerTimestamp ?? source.RetrievedAt ?? DateTimeOffset.MinValue);
    }

    private static bool IsManufacturerCorrespondence(string root, CodexCaseFileInfo file)
    {
        var relative = Path.GetRelativePath(root, file.FullPath);
        return ManufacturerPathMarkers.Any(marker =>
            relative.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTextFile(string path) => Path.GetExtension(path) is ".txt" or ".md" or ".eml";

    private static bool IsManufacturerResponseSource(SearchSource source)
    {
        var location = $"{source.Title} {source.FilePath}";
        return ManufacturerPathMarkers.Any(marker => location.Contains(marker, StringComparison.OrdinalIgnoreCase))
            && LooksLikeManufacturerResponse(source.Text);
    }

    private static string ReadHeader(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaximumHeaderCharacters];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    private static bool ContainsCaseValue(CodexCaseFileInfo file, string text, string supportId) =>
        file.FileName.Contains(supportId, StringComparison.OrdinalIgnoreCase)
        || file.RelativePath.Contains(supportId, StringComparison.OrdinalIgnoreCase)
        || text.Contains(supportId, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeManufacturerResponse(string text) =>
        text.Contains("回答", StringComparison.OrdinalIgnoreCase)
        || text.Contains("response", StringComparison.OrdinalIgnoreCase)
        || text.Contains("reply", StringComparison.OrdinalIgnoreCase)
        || text.Contains("From:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("差出人:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("送信者:", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseSender(string? value, out string displayName, out string emailAddress)
    {
        displayName = string.Empty;
        emailAddress = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Address.Match(value.Trim().Trim('"'));
        if (!match.Success)
        {
            displayName = value.Trim();
            return !string.IsNullOrWhiteSpace(displayName);
        }

        displayName = match.Groups["name"].Success
            ? match.Groups["name"].Value.Trim().Trim('"')
            : string.Empty;
        emailAddress = match.Groups["email"].Success
            ? match.Groups["email"].Value.Trim()
            : match.Groups["emailOnly"].Value.Trim();
        return !string.IsNullOrWhiteSpace(displayName);
    }

    private static bool IsExcludedSender(
        string displayName,
        string emailAddress,
        string companyName,
        string customerName)
    {
        var candidate = $"{displayName} {emailAddress}";
        return candidate.Contains("東陽", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("toyo", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(companyName)
                && candidate.Contains(companyName, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(customerName)
                && candidate.Contains(customerName, StringComparison.OrdinalIgnoreCase));
    }

    private static ManufacturerRecipient Unresolved() => new();

    private sealed record RecipientCandidate(
        CodexCaseFileInfo File,
        string DisplayName,
        string EmailAddress,
        DateTimeOffset Timestamp);

    private sealed record SourceRecipientCandidate(
        string SourceFileName,
        string DisplayName,
        string EmailAddress,
        DateTimeOffset Timestamp);
}
