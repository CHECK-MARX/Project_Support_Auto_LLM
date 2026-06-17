using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Drafts;

public sealed partial class AiDraftStore : IAiDraftStore
{
    private const string DraftsFolderName = "drafts";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Func<DateTimeOffset> nowProvider;

    public AiDraftStore(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public async Task<string> SaveAsync(
        AnswerDraftRequest request,
        AnswerDraftResult result,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (string.IsNullOrWhiteSpace(request.Settings.AiDataFolder))
        {
            throw new ArgumentException("AI data folder is required.", nameof(request));
        }

        var savedAt = nowProvider();
        var draftsFolder = Path.Combine(request.Settings.AiDataFolder, DraftsFolderName);
        Directory.CreateDirectory(draftsFolder);

        var filePath = CreateUniqueDraftPath(draftsFolder, savedAt, request.Case.SupportNumber);
        var document = BuildDocument(savedAt, request, result);

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        return filePath;
    }

    private static AiDraftDocument BuildDocument(
        DateTimeOffset savedAt,
        AnswerDraftRequest request,
        AnswerDraftResult result)
    {
        return new AiDraftDocument
        {
            SavedAt = savedAt,
            Case = SanitizeCase(request.Case),
            InquiryText = RemoveApiKeyLikeSecrets(request.InquiryText),
            CustomerReplyDraft = RemoveApiKeyLikeSecrets(result.CustomerReplyDraft),
            InternalMemo = RemoveApiKeyLikeSecrets(result.InternalMemo),
            NeedConfirmations = result.NeedConfirmations.Select(SanitizeNeedConfirmation).ToList(),
            Evidence = result.Evidence.Select(SanitizeEvidence).ToList(),
            Confidence = result.Confidence,
            Warnings = result.Warnings.Select(RemoveApiKeyLikeSecrets).ToList(),
            Provider = BuildProviderSummary(request.Settings.LlmProvider),
        };
    }

    private static CaseContext SanitizeCase(CaseContext context)
    {
        return context with
        {
            Source = RemoveApiKeyLikeSecrets(context.Source),
            ProductName = RemoveApiKeyLikeSecrets(context.ProductName),
            BaseFolder = RemoveApiKeyLikeSecrets(context.BaseFolder),
            CloseFolder = RemoveApiKeyLikeSecrets(context.CloseFolder),
            CaseFolderPath = RemoveApiKeyLikeSecrets(context.CaseFolderPath),
            CompanyName = RemoveApiKeyLikeSecrets(context.CompanyName),
            CustomerName = RemoveApiKeyLikeSecrets(context.CustomerName),
            SupportNumber = RemoveApiKeyLikeSecrets(context.SupportNumber),
            Status = RemoveApiKeyLikeSecrets(context.Status),
            SelectedText = RemoveApiKeyLikeSecrets(context.SelectedText),
            Notes = context.Notes.Select(SanitizeNote).ToList(),
        };
    }

    private static NoteSnapshot SanitizeNote(NoteSnapshot note)
    {
        return note with
        {
            NoteKind = RemoveApiKeyLikeSecrets(note.NoteKind),
            FilePath = RemoveApiKeyLikeSecrets(note.FilePath),
            FileName = RemoveApiKeyLikeSecrets(note.FileName),
            Text = RemoveApiKeyLikeSecrets(note.Text),
        };
    }

    private static NeedConfirmationItem SanitizeNeedConfirmation(NeedConfirmationItem item)
    {
        return item with
        {
            Question = RemoveApiKeyLikeSecrets(item.Question),
            Reason = RemoveApiKeyLikeSecrets(item.Reason),
            Priority = RemoveApiKeyLikeSecrets(item.Priority),
            RelatedSourceIds = item.RelatedSourceIds.Select(RemoveApiKeyLikeSecrets).ToList(),
        };
    }

    private static EvidenceItem SanitizeEvidence(EvidenceItem item)
    {
        return item with
        {
            SourceId = RemoveApiKeyLikeSecrets(item.SourceId),
            SourceType = RemoveApiKeyLikeSecrets(item.SourceType),
            Title = RemoveApiKeyLikeSecrets(item.Title),
            Excerpt = RemoveApiKeyLikeSecrets(item.Excerpt),
            FilePath = RemoveApiKeyLikeSecrets(item.FilePath),
            SupportNumber = RemoveApiKeyLikeSecrets(item.SupportNumber),
        };
    }

    private static AiDraftProviderSummary BuildProviderSummary(LlmProviderSettings settings)
    {
        return new AiDraftProviderSummary
        {
            Provider = RemoveApiKeyLikeSecrets(settings.Provider),
            Endpoint = RemoveApiKeyLikeSecrets(settings.Endpoint),
            ChatModel = RemoveApiKeyLikeSecrets(settings.ChatModel),
            EmbeddingModel = RemoveApiKeyLikeSecrets(settings.EmbeddingModel),
            TimeoutSeconds = settings.TimeoutSeconds,
            Temperature = settings.Temperature,
            MaxOutputTokens = settings.MaxOutputTokens,
            ContextWindowTokens = settings.ContextWindowTokens,
            ApiKeyEnvironmentVariable = RemoveApiKeyLikeSecrets(settings.ApiKeyEnvironmentVariable),
        };
    }

    private static string CreateUniqueDraftPath(
        string draftsFolder,
        DateTimeOffset savedAt,
        string? supportNumber)
    {
        var timestamp = savedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safeSupportNumber = SanitizeFileNameComponent(supportNumber);
        var baseName = $"{timestamp}_{safeSupportNumber}_answer-draft";
        var candidate = Path.Combine(draftsFolder, $"{baseName}.json");
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(draftsFolder, $"{baseName}_{counter:D3}.json");
            counter += 1;
        }

        return candidate;
    }

    private static string SanitizeFileNameComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Trim()
            .Select(ch => invalidChars.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string RemoveApiKeyLikeSecrets(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return ApiKeyRegex().Replace(value, "[API key redacted]");
    }

    [GeneratedRegex(@"(?:sk-[A-Za-z0-9_-]{12,}|Bearer\s+[A-Za-z0-9._-]{12,}|(?:api[_-]?key|apikey)\s*[:=]\s*[A-Za-z0-9._-]{8,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyRegex();
}
