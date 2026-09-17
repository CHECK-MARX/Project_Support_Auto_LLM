using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Core.Cases;

public enum GptHandoffSection
{
    ManufacturerContact,
    NewlyEstablishedFacts,
    UnresolvedItems,
    ResolvedItems,
    LatestManufacturerResponseSummary,
    CustomerHandlingNotes,
    NextAction,
}

public sealed record GptHandoffSnapshot(
    IReadOnlyDictionary<GptHandoffSection, string> Sections)
{
    public string this[GptHandoffSection section] =>
        Sections.TryGetValue(section, out var value) ? value : string.Empty;
}

public static class GptHandoffFormat
{
    public const string StartMarker = "<<<AI_HANDOFF_V1>>>";
    public const string EndMarker = "<<<END_AI_HANDOFF_V1>>>";
    public const string ImportVersion = "AI_HANDOFF_V1";

    public static readonly IReadOnlyList<GptHandoffSection> SectionOrder =
    [
        GptHandoffSection.ManufacturerContact,
        GptHandoffSection.NewlyEstablishedFacts,
        GptHandoffSection.UnresolvedItems,
        GptHandoffSection.ResolvedItems,
        GptHandoffSection.LatestManufacturerResponseSummary,
        GptHandoffSection.CustomerHandlingNotes,
        GptHandoffSection.NextAction,
    ];

    public static string Label(GptHandoffSection section) => section switch
    {
        GptHandoffSection.ManufacturerContact => "メーカー担当者",
        GptHandoffSection.NewlyEstablishedFacts => "新たに判明した事項",
        GptHandoffSection.UnresolvedItems => "現在の未解決事項",
        GptHandoffSection.ResolvedItems => "解決済みに変更した事項",
        GptHandoffSection.LatestManufacturerResponseSummary => "メーカー最新回答の要旨",
        GptHandoffSection.CustomerHandlingNotes => "お客様対応上の注意事項",
        GptHandoffSection.NextAction => "現在の次アクション",
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };
}

public static class GptHandoffParser
{
    private static readonly IReadOnlyDictionary<string, GptHandoffSection> SectionsByHeader =
        GptHandoffFormat.SectionOrder.ToDictionary(
            section => $"【{GptHandoffFormat.Label(section)}】",
            section => section,
            StringComparer.Ordinal);

    public static bool TryParseClipboard(
        string? clipboardText,
        out GptHandoffSnapshot snapshot,
        out string error)
    {
        snapshot = new GptHandoffSnapshot(new Dictionary<GptHandoffSection, string>());
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            error = "GPTで生成された引継ぎ情報をコピーしてから取り込んでください。";
            return false;
        }

        var normalized = NormalizeNewlines(clipboardText);
        var searchStart = 0;
        var markerPairFound = false;
        var lastParseError = string.Empty;
        while (searchStart < normalized.Length)
        {
            var start = normalized.IndexOf(GptHandoffFormat.StartMarker, searchStart, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var payloadStart = start + GptHandoffFormat.StartMarker.Length;
            var end = normalized.IndexOf(GptHandoffFormat.EndMarker, payloadStart, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            markerPairFound = true;
            var payload = normalized[payloadStart..end];
            if (TryParseSections(payload, requireAllSections: true, out snapshot, out lastParseError))
            {
                return true;
            }

            searchStart = end + GptHandoffFormat.EndMarker.Length;
        }

        if (!markerPairFound)
        {
            error = "Clipboardに有効なAI_HANDOFF_V1形式がありません。";
            return false;
        }

        error = string.IsNullOrWhiteSpace(lastParseError)
            ? "Clipboardに有効なAI_HANDOFF_V1形式がありません。"
            : lastParseError;
        return false;
    }

    public static bool TryParseStoredEntry(string? value, out GptHandoffSnapshot snapshot) =>
        TryParseSections(value ?? string.Empty, requireAllSections: false, out snapshot, out _);

    public static bool TryBuildEffectiveSnapshot(
        string? historyText,
        out GptHandoffSnapshot snapshot,
        out int validEntryCount)
    {
        GptHandoffSnapshot? effective = null;
        validEntryCount = 0;
        foreach (var entry in CaseNoteHistoryParser.Parse(historyText).OrderBy(static item => item.Index))
        {
            if (!TryParseStoredEntry(entry.Body, out var partial))
            {
                continue;
            }

            effective = effective is null ? partial : Overlay(effective, partial);
            validEntryCount++;
        }

        snapshot = effective ?? new GptHandoffSnapshot(new Dictionary<GptHandoffSection, string>());
        return effective is not null;
    }

    public static string Canonicalize(GptHandoffSnapshot snapshot)
    {
        var builder = new StringBuilder();
        foreach (var section in GptHandoffFormat.SectionOrder)
        {
            builder.Append("【").Append(GptHandoffFormat.Label(section)).AppendLine("】");
            builder.AppendLine(NormalizeSection(snapshot[section]));
        }

        return builder.ToString().TrimEnd();
    }

    public static string ComputeHash(GptHandoffSnapshot snapshot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(snapshot)));
        return Convert.ToHexString(bytes);
    }

    public static GptHandoffSnapshot Overlay(
        GptHandoffSnapshot baseline,
        GptHandoffSnapshot update)
    {
        var merged = baseline.Sections.ToDictionary(static item => item.Key, static item => item.Value);
        foreach (var item in update.Sections)
        {
            merged[item.Key] = item.Value;
        }

        return new GptHandoffSnapshot(merged);
    }

    public static GptHandoffSnapshot ChangedSections(
        GptHandoffSnapshot? previous,
        GptHandoffSnapshot current)
    {
        if (previous is null)
        {
            return current;
        }

        var changed = GptHandoffFormat.SectionOrder
            .Where(section => !string.Equals(
                NormalizeSection(previous[section]),
                NormalizeSection(current[section]),
                StringComparison.Ordinal))
            .ToDictionary(section => section, section => current[section]);
        return new GptHandoffSnapshot(changed);
    }

    private static bool TryParseSections(
        string value,
        bool requireAllSections,
        out GptHandoffSnapshot snapshot,
        out string error)
    {
        var parsed = new Dictionary<GptHandoffSection, string>();
        var currentLines = new List<string>();
        GptHandoffSection? currentSection = null;
        error = string.Empty;

        foreach (var rawLine in NormalizeNewlines(value).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var structuralLine = NormalizeStructuralLine(line);
            if (IsMarkdownFence(structuralLine) || structuralLine is "**" or "__")
            {
                continue;
            }

            if (SectionsByHeader.TryGetValue(structuralLine, out var section))
            {
                if (currentSection is not null)
                {
                    parsed[currentSection.Value] = NormalizeSection(string.Join('\n', currentLines));
                }

                if (parsed.ContainsKey(section) || currentSection == section)
                {
                    snapshot = new GptHandoffSnapshot(parsed);
                    error = $"引継ぎ情報のセクションが重複しています: {GptHandoffFormat.Label(section)}";
                    return false;
                }

                currentSection = section;
                currentLines.Clear();
                continue;
            }

            if (structuralLine.StartsWith('【') && structuralLine.EndsWith('】'))
            {
                snapshot = new GptHandoffSnapshot(parsed);
                error = $"未対応の引継ぎ情報セクションです: {structuralLine}";
                return false;
            }

            if (currentSection is null)
            {
                if (!string.IsNullOrWhiteSpace(structuralLine))
                {
                    snapshot = new GptHandoffSnapshot(parsed);
                    error = "引継ぎ情報のセクション形式が不正です。";
                    return false;
                }

                continue;
            }

            currentLines.Add(line);
        }

        if (currentSection is not null)
        {
            parsed[currentSection.Value] = NormalizeSection(string.Join('\n', currentLines));
        }

        if (parsed.Count == 0)
        {
            snapshot = new GptHandoffSnapshot(parsed);
            error = "引継ぎ情報のセクションを確認できません。";
            return false;
        }

        if (requireAllSections)
        {
            var missing = GptHandoffFormat.SectionOrder
                .Where(section => !parsed.ContainsKey(section))
                .Select(GptHandoffFormat.Label)
                .ToList();
            if (missing.Count > 0)
            {
                snapshot = new GptHandoffSnapshot(parsed);
                error = $"引継ぎ情報に不足があります: {string.Join("、", missing)}";
                return false;
            }

            if (parsed.Values.All(string.IsNullOrWhiteSpace))
            {
                snapshot = new GptHandoffSnapshot(parsed);
                error = "引継ぎ情報の内容が空です。";
                return false;
            }
        }

        snapshot = new GptHandoffSnapshot(parsed);
        return true;
    }

    private static string NormalizeSection(string? value)
    {
        var lines = NormalizeNewlines(value ?? string.Empty)
            .Split('\n')
            .Select(static line => line.Trim())
            .ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var normalized = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length > 0 || normalized.Count == 0 || normalized[^1].Length > 0)
            {
                normalized.Add(line);
            }
        }

        return string.Join('\n', normalized);
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizeStructuralLine(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("> ", StringComparison.Ordinal))
        {
            normalized = normalized[2..].Trim();
        }

        if (normalized.Length >= 4 &&
            ((normalized.StartsWith("**", StringComparison.Ordinal) && normalized.EndsWith("**", StringComparison.Ordinal)) ||
             (normalized.StartsWith("__", StringComparison.Ordinal) && normalized.EndsWith("__", StringComparison.Ordinal))))
        {
            normalized = normalized[2..^2].Trim();
        }

        return normalized;
    }

    private static bool IsMarkdownFence(string value) =>
        value.StartsWith("```", StringComparison.Ordinal);
}

public enum GptHandoffImportStatus
{
    Imported,
    NoChange,
    Blocked,
    Failed,
}

public sealed record GptHandoffImportResult(
    GptHandoffImportStatus Status,
    string Message,
    string FilePath,
    string LastImportedHash,
    string LastImportedAt,
    int ImportVersion,
    IReadOnlyList<GptHandoffSection> ChangedSections);

public sealed class GptHandoffImportService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CaseLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> clock;

    public GptHandoffImportService(Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<GptHandoffImportResult> ImportAsync(
        CaseRecord caseRecord,
        GptHandoffSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseRecord);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryResolveStoragePath(caseRecord, out var filePath, out var validationError))
        {
            return Failed(GptHandoffImportStatus.Blocked, validationError);
        }

        var caseLock = CaseLocks.GetOrAdd(filePath, static _ => new SemaphoreSlim(1, 1));
        await caseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadExistingAsync(filePath, cancellationToken).ConfigureAwait(false);
            var hash = GptHandoffParser.ComputeHash(snapshot);
            var existingHash = existing.EffectiveSnapshot is null
                ? string.Empty
                : GptHandoffParser.ComputeHash(existing.EffectiveSnapshot);
            if (string.Equals(caseRecord.GptRegistration.LastImportedHash, hash, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return new GptHandoffImportResult(
                    GptHandoffImportStatus.NoChange,
                    "前回取り込み済みです。新しい情報はありません。",
                    filePath,
                    hash,
                    caseRecord.GptRegistration.LastImportedAt,
                    Math.Max(caseRecord.GptRegistration.ImportVersion, existing.ValidEntryCount),
                    []);
            }

            var changes = GptHandoffParser.ChangedSections(existing.EffectiveSnapshot, snapshot);
            if (changes.Sections.Count == 0)
            {
                return new GptHandoffImportResult(
                    GptHandoffImportStatus.NoChange,
                    "前回取り込み済みです。新しい情報はありません。",
                    filePath,
                    hash,
                    caseRecord.GptRegistration.LastImportedAt,
                    Math.Max(caseRecord.GptRegistration.ImportVersion, existing.ValidEntryCount),
                    []);
            }

            var importedAt = clock();
            var version = Math.Max(caseRecord.GptRegistration.ImportVersion, existing.ValidEntryCount) + 1;
            await AppendEntryAsync(filePath, changes, importedAt, cancellationToken).ConfigureAwait(false);
            return new GptHandoffImportResult(
                GptHandoffImportStatus.Imported,
                "GPT引継ぎ情報を取り込みました。",
                filePath,
                hash,
                importedAt.ToString("O"),
                version,
                changes.Sections.Keys.ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failed(GptHandoffImportStatus.Failed, $"GPT引継ぎ情報を保存できませんでした: {ex.Message}");
        }
        finally
        {
            caseLock.Release();
        }
    }

    private static bool TryResolveStoragePath(
        CaseRecord caseRecord,
        out string filePath,
        out string error)
    {
        filePath = string.Empty;
        error = string.Empty;
        var registration = caseRecord.GptRegistration;
        if (!registration.IsRegistered || string.IsNullOrWhiteSpace(registration.ConversationUrl))
        {
            error = "GPT登録済みのConversation URLがありません。";
            return false;
        }

        if (!string.Equals(registration.SupportId, caseRecord.SupportNumber, StringComparison.OrdinalIgnoreCase))
        {
            error = "GPT登録情報と現在案件のSupport IDが一致しません。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(caseRecord.FolderPath) || !Directory.Exists(caseRecord.FolderPath))
        {
            error = "案件フォルダが見つかりません。";
            return false;
        }

        var supportId = CaseNaming.FormatSupportNumber(caseRecord.SupportNumber);
        if (string.IsNullOrWhiteSpace(supportId) || supportId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Support IDから安全な保存先を作成できません。";
            return false;
        }

        var root = Path.GetFullPath(caseRecord.FolderPath).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, $"GPT連携内容_{supportId}.txt"));
        if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
        {
            error = "GPT引継ぎ情報の保存先が案件フォルダ外です。";
            return false;
        }

        filePath = candidate;
        return true;
    }

    private static async Task<(GptHandoffSnapshot? EffectiveSnapshot, int ValidEntryCount)> ReadExistingAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return (null, 0);
        }

        var text = await File.ReadAllTextAsync(filePath, EncodingPolicy.Utf8NoBom, cancellationToken).ConfigureAwait(false);
        return GptHandoffParser.TryBuildEffectiveSnapshot(text, out var effective, out var count)
            ? (effective, count)
            : (null, 0);
    }

    private static async Task AppendEntryAsync(
        string filePath,
        GptHandoffSnapshot changes,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        var lineEnding = EncodingPolicy.LineEnding;
        var builder = new StringBuilder();
        if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
        {
            builder.Append(lineEnding).Append(lineEnding);
        }

        builder.Append("*****追記部_")
            .Append(importedAt.ToString("yyyy/MM/dd HH:mm:ss"))
            .Append("(GPT取込)******")
            .Append(lineEnding);
        foreach (var section in GptHandoffFormat.SectionOrder.Where(changes.Sections.ContainsKey))
        {
            builder.Append('【').Append(GptHandoffFormat.Label(section)).Append('】').Append(lineEnding);
            builder.Append(changes[section].Replace("\n", lineEnding, StringComparison.Ordinal)).Append(lineEnding).Append(lineEnding);
        }

        builder.Append("--------------------------------------------------").Append(lineEnding);
        await using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, EncodingPolicy.Utf8NoBom);
        await writer.WriteAsync(builder.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GptHandoffImportResult Failed(GptHandoffImportStatus status, string message) =>
        new(status, message, string.Empty, string.Empty, string.Empty, 0, []);
}
