using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.AiAssistant.App.GptHandoff;
using SupportCaseManager.AiAssistant.App.ViewModels;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Config;
using SupportCaseManager.Core.Notes;
using SupportCaseManager.App.ChatGpt;

namespace SupportCaseManager.AiAssistant.App.Diagnostics;

public enum QuickDiagnosticStatus { Pass, Warn, Fail }

public sealed record QuickDiagnosticItem(string Name, QuickDiagnosticStatus Status, string Detail)
{
    public string StatusText => Status.ToString().ToUpperInvariant();
}

public sealed record QuickDiagnosticReport(IReadOnlyList<QuickDiagnosticItem> Items, TimeSpan Elapsed)
{
    public string Overall => Items.Any(item => item.Status == QuickDiagnosticStatus.Fail) ? "FAILED"
        : Items.Any(item => item.Status == QuickDiagnosticStatus.Warn) ? "WARNING" : "HEALTHY";
}

public sealed record QuickDiagnosticSnapshot(
    string SupportId,
    string Product,
    string CaseFolder,
    string BaseFolder,
    string CloseFolder,
    string ProductPromptFilePath,
    string SupportToolSettingsFilePath,
    CaseContext? CurrentCase,
    GptHandoffContext Registration,
    IReadOnlyList<NoteSnapshot> Notes,
    IReadOnlyList<SearchSource> Evidence);

internal sealed class QuickDiagnosticService
{
    public Task<QuickDiagnosticReport> RunAsync(QuickDiagnosticSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.Run(() => Run(snapshot, cancellationToken), cancellationToken);

    private static QuickDiagnosticReport Run(QuickDiagnosticSnapshot snapshot, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var results = new List<QuickDiagnosticItem>();
        void Add(string name, QuickDiagnosticStatus status, string detail) => results.Add(new(name, status, detail));
        void Check() => token.ThrowIfCancellationRequested();

        Check();
        var support = CaseNaming.NormalizeSupportNumber(snapshot.SupportId);
        var hasCase = !string.IsNullOrWhiteSpace(support) && !string.IsNullOrWhiteSpace(snapshot.Product);
        var contextMatches = snapshot.CurrentCase is not null
            && string.Equals(CaseNaming.NormalizeSupportNumber(snapshot.CurrentCase.SupportNumber ?? string.Empty), support, StringComparison.Ordinal)
            && string.Equals(snapshot.CurrentCase.ProductName, snapshot.Product, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(snapshot.CaseFolder) || string.Equals(
                snapshot.CurrentCase.CaseFolderPath, snapshot.CaseFolder, StringComparison.OrdinalIgnoreCase));
        Add("Case Context", !hasCase ? QuickDiagnosticStatus.Warn : contextMatches ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Warn,
            !hasCase ? "案件未選択" : contextMatches ? $"{support} / {snapshot.Product} / CurrentCase一致" : "CurrentCaseが未読込、または案件識別が不一致");
        Add("CurrentCase", !hasCase ? QuickDiagnosticStatus.Warn : contextMatches ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Warn,
            contextMatches ? "現在案件のSupport IDと製品が一致" : "現在案件の一致を確認できません");

        Check();
        var roots = new[] { snapshot.BaseFolder, snapshot.CloseFolder }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var indexRecords = new List<CaseRecordDto>();
        var indexProblems = new List<string>();
        foreach (var root in roots)
        {
            Check();
            try
            {
                var indexPath = Path.Combine(root, "cases-index.json");
                if (!File.Exists(indexPath))
                {
                    indexProblems.Add("index未作成");
                    continue;
                }

                using var json = JsonDocument.Parse(File.ReadAllText(indexPath));
                var elements = json.RootElement.ValueKind == JsonValueKind.Array
                    ? json.RootElement.EnumerateArray().ToArray()
                    : json.RootElement.ValueKind == JsonValueKind.Object ? [json.RootElement] : [];
                if (elements.Length == 0 && json.RootElement.ValueKind != JsonValueKind.Array)
                {
                    indexProblems.Add("JSON形式不正");
                }
                foreach (var element in elements)
                {
                    var record = element.Deserialize<CaseRecordDto>();
                    if (record is null) indexProblems.Add("不正な案件レコード");
                    else indexRecords.Add(record);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                indexProblems.Add($"index読取不可 ({ex.GetType().Name})");
            }
        }

        var duplicates = indexRecords.Where(record => !string.IsNullOrWhiteSpace(record.GptRegistration?.Product))
            .GroupBy(record =>
                $"{CaseNaming.NormalizeSupportNumber(record.SupportNumber)}|{record.GptRegistration.Product}",
                StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1 && !group.Key.StartsWith('|'));
        var invalidUrls = indexRecords.Count(record => record.GptRegistration?.IsRegistered == true
            && !GptConversationUrl.TryValidateConversation(record.GptRegistration.ConversationUrl, out _));
        var brokenReferences = indexRecords.Count(record =>
            !string.IsNullOrWhiteSpace(record.FolderPath) && !Directory.Exists(record.FolderPath));
        if (duplicates > 0) indexProblems.Add($"重複{duplicates}件");
        if (invalidUrls > 0) indexProblems.Add($"Conversation URL不正{invalidUrls}件");
        if (brokenReferences > 0) indexProblems.Add($"保存先未解決{brokenReferences}件");
        if (roots.Length == 0) indexProblems.Add("indexの参照先未設定");
        Add("cases-index", indexProblems.Count == 0 ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Warn,
            indexProblems.Count == 0 ? $"{indexRecords.Count}件読取、変更なし" : string.Join(" / ", indexProblems.Distinct()));

        Check();
        var folder = snapshot.CaseFolder;
        var folderExists = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        var indexedMatch = indexRecords.FirstOrDefault(record =>
            string.Equals(CaseNaming.NormalizeSupportNumber(record.SupportNumber), support, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(record.GptRegistration?.Product)
                || string.Equals(record.GptRegistration.Product, snapshot.Product, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(record.FolderPath) && Directory.Exists(record.FolderPath));
        Add("Case Folder", folderExists ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Warn, folderExists ? "現在フォルダを確認" : indexedMatch is not null
            ? "保存パスは古い可能性。indexの同一Support IDに実在フォルダあり" : "現在フォルダを確認できません。全フォルダ走査は未実行");
        try
        {
            if (folderExists) _ = Directory.EnumerateFileSystemEntries(folder).Take(1).ToArray();
            Add("OneDrive/File Access", folderExists ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Warn,
                folderExists ? "案件フォルダを読取可能" : "案件フォルダへアクセスできません");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Add("OneDrive/File Access", QuickDiagnosticStatus.Fail, $"案件フォルダ読取不可 ({ex.GetType().Name})");
        }

        Check();
        var notes = snapshot.Notes ?? [];
        var histories = notes.Where(note => note.NoteKind.Contains("相談", StringComparison.Ordinal)
                || note.NoteKind.Contains("返信", StringComparison.Ordinal)
                || note.NoteKind.Contains("メーカー", StringComparison.Ordinal))
            .Select(note => (note.NoteKind, Entries: CaseNoteHistoryParser.Parse(note.Text))).ToArray();
        var latestInquiry = histories.Where(item => item.NoteKind.Contains("相談", StringComparison.Ordinal))
            .SelectMany(item => item.Entries).ToArray();
        var delta = CaseNoteHistoryParser.PickLatest(latestInquiry);
        Add("History Parser", !hasCase || notes.Count == 0 ? QuickDiagnosticStatus.Warn : QuickDiagnosticStatus.Pass,
            notes.Count == 0 ? "案件ノート未読込" : $"CurrentCustomerDelta: {(delta is null ? "未解決" : "解析済み")} / 履歴{histories.Sum(item => item.Entries.Count)}件");

        Check();
        var registered = string.Equals(snapshot.Registration.RegistrationState, GptRegistrationStates.Registered, StringComparison.Ordinal);
        var registrationValid = GptHandoffRegistrationResolver.TryResolve(snapshot.Registration, snapshot.SupportId, snapshot.Product, out _, out _);
        var unregistered = string.IsNullOrWhiteSpace(snapshot.Registration.RegistrationState)
            || string.Equals(snapshot.Registration.RegistrationState, GptRegistrationStates.Unregistered, StringComparison.Ordinal);
        var registrationStatus = registered ? registrationValid ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Fail
            : unregistered ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Warn;
        Add("GPT Registration", registrationStatus,
            unregistered ? "UNREGISTERED" : registrationValid ? $"{support} / {snapshot.Product} / REGISTERED" : "登録情報のSupport ID・製品・URLを確認できません");
        Add("GPT Conversation URL", registrationStatus,
            unregistered ? "未登録のため対象外" : registrationValid ? "Conversation URL形式を確認" : "Conversation URLまたは登録識別が不正");

        Check();
        var handoffPath = folderExists && !string.IsNullOrWhiteSpace(support)
            ? Path.Combine(folder, $"GPT連携内容_{support}.txt") : string.Empty;
        if (string.IsNullOrWhiteSpace(handoffPath))
        {
            Add("GPT Handoff", hasCase ? QuickDiagnosticStatus.Warn : QuickDiagnosticStatus.Pass,
                hasCase ? "案件フォルダ未解決のため確認不可" : "案件未選択のため対象外");
        }
        else if (!File.Exists(handoffPath))
        {
            Add("GPT Handoff", QuickDiagnosticStatus.Pass, "取込ファイルなし（正常）");
        }
        else
        {
            try
            {
                var parsed = GptHandoffParser.TryBuildEffectiveSnapshot(File.ReadAllText(handoffPath), out _, out var count);
                Add("GPT Handoff", parsed ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Warn,
                    parsed ? $"有効な履歴{count}件を解析" : "既存parserで有効な引継ぎを確認できません");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Add("GPT Handoff", QuickDiagnosticStatus.Warn, $"読取不可 ({ex.GetType().Name})");
            }
        }

        var sourceTypes = snapshot.Evidence.Select(source => source.SourceType).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var recognizedTypes = new[] { "CurrentCase", "GptHandoff", "Manual", "OfficialDoc", "PastCaseNote", "PastAnswer", "ExactPastAnswer", "CuratedFact" };
        var unknownTypes = sourceTypes.Count(type => !recognizedTypes.Contains(type, StringComparer.OrdinalIgnoreCase));
        Add("RAG Integration", QuickDiagnosticStatus.Warn,
            sourceTypes.Length == 0 ? "選択済み根拠なし。LLM・indexにはアクセスしていません"
                : unknownTypes > 0 ? $"未認識SourceType {unknownTypes}種類" : $"既知のSourceType {sourceTypes.Length}種類。実際のcontext compositionは未検証");

        Check();
        AddRoute("Customer Reply Routing", "メーカーへ依頼済みです。回答は9月24日以降となる見込みです。その旨をお客様へ連絡してください。",
            NaturalLanguageOperation.CustomerStatusUpdate, requiresResponse: false);
        AddRoute("Manufacturer ASK", "メーカーへこの内容を確認するメールを作成してください。", NaturalLanguageOperation.ManufacturerAsk);
        AddRoute("Manufacturer REPLY", "以下のメーカー回答に対して御礼の返信を作成してください。", NaturalLanguageOperation.ManufacturerReply, requiresResponse: true);
        AddRoute("Manufacturer Translation", "以下のメーカー回答を日本語にしてください。", NaturalLanguageOperation.ManufacturerResponseTranslation, requiresResponse: true);
        AddRoute("Normal Chat Isolation", "このエラーの原因を教えてください。", NaturalLanguageOperation.NormalChat);
        Add("Artifact Isolation", QuickDiagnosticStatus.Warn, "成果物計画は未実行。実際のSend経路の状態不変は未検証");

        Check();
        var productInstruction = SupportPromptFileLoader.Load(
            snapshot.ProductPromptFilePath, snapshot.SupportToolSettingsFilePath);
        Add("Product Instruction", string.IsNullOrWhiteSpace(productInstruction.ProductInstruction) ? QuickDiagnosticStatus.Warn : QuickDiagnosticStatus.Pass,
            string.IsNullOrWhiteSpace(productInstruction.ProductInstruction) ? "製品別指示ファイルを読込できません"
                : $"{Path.GetFileName(productInstruction.ProductResolvedPath)} を読込");
        Add("UI Blocking Check", QuickDiagnosticStatus.Warn,
            "診断はバックグラウンド実行。親WPFの案件選択時間は実GUIで確認してください");

        return new QuickDiagnosticReport(results, timer.Elapsed);

        void AddRoute(string name, string instruction, NaturalLanguageOperation expected, bool requiresResponse = false)
        {
            var resolved = NaturalLanguageOperationResolver.ResolveIntent(instruction);
            var passed = resolved.Operation == expected && resolved.RequiresManufacturerResponse == requiresResponse;
            Add(name, passed ? QuickDiagnosticStatus.Pass : QuickDiagnosticStatus.Fail,
                passed ? $"{expected} / 回答本文{(requiresResponse ? "要" : "不要")}" : $"実際: {resolved.Operation} / 回答本文{(resolved.RequiresManufacturerResponse ? "要" : "不要")}");
        }
    }
}
