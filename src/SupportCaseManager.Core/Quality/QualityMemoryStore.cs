using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Config;

namespace SupportCaseManager.Core.Quality;

public static class QualityAudience
{
    public const string Customer = "CUSTOMER";
    public const string Manufacturer = "MANUFACTURER";
}

public static class QualityDirection
{
    public const string CustomerOutbound = "OUTBOUND_TO_CUSTOMER";
    public const string ManufacturerOutbound = "OUTBOUND_TO_MANUFACTURER";
    public const string ManufacturerInbound = "INBOUND_FROM_MANUFACTURER";
    public const string Unknown = "UNKNOWN";
}

public sealed record QualityDraftSnapshot
{
    public string DraftId { get; init; } = Guid.NewGuid().ToString("N");
    public string SupportId { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string Intent { get; init; } = string.Empty;
    public string DraftText { get; init; } = string.Empty;
    public string DraftHash { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record QualityMemoryRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Product { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string Intent { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string ApprovedText { get; init; } = string.Empty;
    public string ReusableStyleText { get; init; } = string.Empty;
    public string SupportId { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public DateTimeOffset? SourceBlockTimestamp { get; init; }
    public string SourceBlockHash { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string Origin { get; init; } = "UNKNOWN";
    public DateTimeOffset ApprovedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ApprovedByUser { get; init; }
    public string? SupersedesId { get; init; }
    public string? OriginalDraftId { get; init; }
    public IReadOnlyList<string> CorrectionTags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record QualityApprovalRequest(
    string Product,
    string Audience,
    string Intent,
    string Direction,
    string SupportId,
    string SourceFile,
    DateTimeOffset? SourceBlockTimestamp,
    string SourceBlockHash,
    string ApprovedText,
    string Origin = "UNKNOWN",
    string? SupersedesId = null);

public sealed record QualityMemoryDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<QualityMemoryRecord> Records { get; init; } = [];
    public IReadOnlyList<QualityDraftSnapshot> Drafts { get; init; } = [];
}

public sealed class QualityMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string filePath;

    public QualityMemoryStore(string? filePath = null)
    {
        this.filePath = filePath ?? Path.Combine(
            Path.GetDirectoryName(new ConfigStore().SettingsPath)!, "quality-memory-v1.json");
    }

    public string FilePath => filePath;

    public QualityMemoryDocument Load()
    {
        if (!File.Exists(filePath)) return new QualityMemoryDocument();
        var document = JsonSerializer.Deserialize<QualityMemoryDocument>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidDataException("Quality Memory store is empty.");
        if (document.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported Quality Memory schema version.");
        return document;
    }

    public Task<QualityDraftSnapshot> SaveDraftAsync(
        string supportId, string product, string audience, string intent, string draftText,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(draftText) || string.IsNullOrWhiteSpace(product)
            || string.IsNullOrWhiteSpace(CaseNaming.NormalizeSupportNumber(supportId)))
            throw new ArgumentException("Draft case, product and text are required.");
        var snapshot = new QualityDraftSnapshot
        {
            SupportId = CaseNaming.NormalizeSupportNumber(supportId),
            Product = product.Trim(), Audience = audience, Intent = intent,
            DraftText = draftText.Trim(), DraftHash = Hash(draftText.Trim()),
        };
        Mutate(document => document with { Drafts = document.Drafts.Append(snapshot).TakeLast(200).ToArray() }, cancellationToken);
        return snapshot;
    }, cancellationToken);

    public Task<QualityMemoryRecord> ApproveAsync(QualityApprovalRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => Approve(request, cancellationToken), cancellationToken);

    private QualityMemoryRecord Approve(QualityApprovalRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApprovedText) || string.IsNullOrWhiteSpace(request.Product)
            || string.IsNullOrWhiteSpace(request.Intent) || string.IsNullOrWhiteSpace(request.SupportId)
            || string.IsNullOrWhiteSpace(request.SourceFile))
            throw new ArgumentException("Approval source and text are required.");
        if (request.Audience == QualityAudience.Customer && request.Direction != QualityDirection.CustomerOutbound
            || request.Audience == QualityAudience.Manufacturer && request.Direction != QualityDirection.ManufacturerOutbound
            || request.Audience != QualityAudience.Customer && request.Audience != QualityAudience.Manufacturer)
            throw new InvalidOperationException("Only explicitly confirmed outbound text can be approved.");
        if (request.Audience == QualityAudience.Manufacturer
            && request.Intent is not ("MANUFACTURER_ASK" or "MANUFACTURER_REPLY"))
            throw new InvalidOperationException("Manufacturer intent is required.");
        if (request.Audience == QualityAudience.Customer
            && request.Intent is not ("CUSTOMER_REPLY" or "CUSTOMER_STATUS_UPDATE"))
            throw new InvalidOperationException("Customer intent is required.");
        var sourceHash = Hash(request.ApprovedText);
        if (!string.Equals(sourceHash, request.SourceBlockHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved source block changed; approval was stopped.");

        QualityMemoryRecord? saved = null;
        Mutate(document =>
        {
            var duplicate = document.Records.FirstOrDefault(record =>
                record.SourceBlockHash == sourceHash && record.Audience == request.Audience
                && record.Intent == request.Intent);
            if (duplicate is not null)
            {
                saved = duplicate;
                return document;
            }
            if (request.SupersedesId is not null && !document.Records.Any(record =>
                    record.Id == request.SupersedesId && record.Audience == request.Audience
                    && record.Intent == request.Intent && record.Product == request.Product))
                throw new InvalidOperationException("Superseded record does not match this product and intent.");

            var matchingDrafts = request.Origin is "AI_DRAFT_EDITED" or "GPT_REVISED"
                ? document.Drafts.Where(draft => draft.SupportId == CaseNaming.NormalizeSupportNumber(request.SupportId)
                    && draft.Product.Equals(request.Product, StringComparison.OrdinalIgnoreCase)
                    && draft.Audience == request.Audience && draft.Intent == request.Intent
                    && request.SourceBlockTimestamp.HasValue
                    && draft.CreatedAt <= request.SourceBlockTimestamp.Value
                    && DateTimeOffset.UtcNow - draft.CreatedAt < TimeSpan.FromDays(2))
                    .Where(draft => !document.Records.Any(record => record.OriginalDraftId == draft.DraftId))
                    .OrderByDescending(draft => draft.CreatedAt).Take(2).ToArray()
                : [];
            var original = matchingDrafts.Length == 1 ? matchingDrafts[0] : null;
            saved = new QualityMemoryRecord
            {
                Product = request.Product.Trim(), Audience = request.Audience, Intent = request.Intent,
                Direction = request.Direction, ApprovedText = request.ApprovedText.Trim(),
                ReusableStyleText = QualityStyleSummary.Build(request.ApprovedText, request.Audience),
                SupportId = CaseNaming.NormalizeSupportNumber(request.SupportId),
                SourceFile = request.SourceFile, SourceBlockTimestamp = request.SourceBlockTimestamp,
                SourceBlockHash = sourceHash,
                SourceType = request.Audience == QualityAudience.Customer
                    ? "CUSTOMER_REPLY_APPROVED" : "MANUFACTURER_OUTBOUND_APPROVED",
                Origin = request.Origin, ApprovedByUser = true,
                SupersedesId = request.SupersedesId,
                OriginalDraftId = original?.DraftId,
                CorrectionTags = original is null ? [] : QualityCorrectionAnalyzer.Analyze(original.DraftText, request.ApprovedText),
            };
            return document with { Records = document.Records.Append(saved).ToArray() };
        }, cancellationToken);
        return saved!;
    }

    public IReadOnlyList<QualityMemoryRecord> Retrieve(string product, string audience, string intent, int max = 3)
    {
        var records = Load().Records;
        var requiredDirection = audience switch
        {
            QualityAudience.Customer => QualityDirection.CustomerOutbound,
            QualityAudience.Manufacturer => QualityDirection.ManufacturerOutbound,
            _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(intent) ||
            string.IsNullOrWhiteSpace(requiredDirection))
        {
            return [];
        }

        var supersededIds = records.Select(record => record.SupersedesId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        return records.Where(record => record.ApprovedByUser && !supersededIds.Contains(record.Id)
                && record.Audience == audience && record.Direction == requiredDirection
                && record.Intent == intent && record.Product.Equals(product, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(record.ReusableStyleText)
                && string.Equals(record.ReusableStyleText,
                    QualityStyleSummary.Build(record.ApprovedText, record.Audience), StringComparison.Ordinal))
            .OrderByDescending(record => record.ApprovedAt)
            .Take(Math.Clamp(max, 0, 3)).ToArray();
    }

    private void Mutate(Func<QualityMemoryDocument, QualityMemoryDocument> update, CancellationToken token)
    {
        var lockPath = filePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var gate = AcquireLock(lockPath, token);
        var before = Load();
        var after = update(before);
        if (ReferenceEquals(before, after)) return;
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(after, JsonOptions), new UTF8Encoding(false));
            File.Move(temp, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static FileStream AcquireLock(string path, CancellationToken token)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99) { Thread.Sleep(50); }
        }
        throw new IOException("Quality Memory store is busy.");
    }

    public static string Hash(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim()))).ToLowerInvariant();
}

public static class QualityStyleSummary
{
    public static string Build(string approvedText, string audience)
    {
        var paragraphs = approvedText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bulletCount = approvedText.Split('\n').Count(line =>
            line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("・", StringComparison.Ordinal));
        var parts = new List<string>
        {
            audience == QualityAudience.Manufacturer ? "英語の業務メールとして簡潔に書く。" : "丁寧な日本語の業務メールとして書く。",
            paragraphs.Length > 2 ? "話題ごとに段落を分ける。" : "短い段落でまとめる。",
        };
        if (bulletCount > 1) parts.Add("複数の確認事項は箇条書きで整理する。");
        if (approvedText.Contains('？') || approvedText.Contains('?')) parts.Add("必要な確認事項を明確に分ける。");
        if (approvedText.Contains("恐れ入ります", StringComparison.Ordinal)
            || approvedText.Contains("please", StringComparison.OrdinalIgnoreCase))
            parts.Add("依頼表現は丁寧にする。");
        return string.Join(' ', parts);
    }
}

public static class QualityCorrectionAnalyzer
{
    public static IReadOnlyList<string> Analyze(string draft, string approved)
    {
        var tags = new List<string>();
        static IReadOnlySet<string> TechnicalValues(string text) => Regex.Matches(text,
                @"(?i)(?<![\w])(?:\d+(?:\.\d+)+|CVE-\d{4}-\d+|--[a-z][\w-]*)(?![\w])")
            .Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (approved.Length < draft.Length * 0.8) tags.Add("CONCISENESS");
        if (approved.Split('\n').Length > draft.Split('\n').Length + 1) tags.Add("STRUCTURE");
        if (draft.Count(character => character is '?' or '？') > approved.Count(character => character is '?' or '？'))
            tags.Add("QUESTION_REMOVAL");
        if (!TechnicalValues(draft).SetEquals(TechnicalValues(approved)))
            tags.Add("TECHNICAL_VALUE_CHANGE_REVIEW_REQUIRED");
        if (!string.Equals(draft.Trim(), approved.Trim(), StringComparison.Ordinal) && tags.Count == 0)
            tags.Add("CLARITY");
        return tags;
    }
}
