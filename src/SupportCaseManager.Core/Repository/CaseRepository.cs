using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Logging;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Core.Repository;

public sealed class CaseRepository
{
    private readonly IAppLogger _logger;
    private string? _basePath;
    private string? _indexPath;
    private List<CaseRecord> _caseIndex = new();

    public CaseRepository(IAppLogger logger)
    {
        _logger = logger;
    }

    public string? BasePath => _basePath;

    public void SetBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            _basePath = null;
            _indexPath = null;
            _caseIndex = new List<CaseRecord>();
            return;
        }

        var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(basePath));
        Directory.CreateDirectory(resolved);
        _basePath = resolved;
        _indexPath = Path.Combine(resolved, "cases-index.json");
        _caseIndex = LoadIndex();
    }

    public IReadOnlyList<string> ListCategories()
    {
        if (string.IsNullOrEmpty(_basePath) || !Directory.Exists(_basePath))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateDirectories(_basePath)
            .Select(path => new DirectoryInfo(path).Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public List<CaseRecord> AllCases()
    {
        var filesystem = ScanFolders();
        var merged = MergeCases(_caseIndex, filesystem);
        _caseIndex = merged.Select(item => item.CloneWith(isFromFolder: false)).ToList();
        SaveIndex();
        return merged;
    }

    public CaseRecord? FindBySupport(string supportNumber)
    {
        var normalized = CaseNaming.NormalizeSupportNumber(supportNumber);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        foreach (var caseRecord in _caseIndex)
        {
            if (caseRecord.NormalizedSupport == normalized)
            {
                return caseRecord;
            }
        }

        foreach (var caseRecord in ScanFolders())
        {
            if (caseRecord.NormalizedSupport == normalized)
            {
                return caseRecord;
            }
        }

        return null;
    }

    public CaseRecord CreateCase(
        string company,
        string supportNumber,
        string status,
        string createdOn,
        string category = "",
        bool openAfter = false)
    {
        if (string.IsNullOrEmpty(_basePath))
        {
            throw new InvalidOperationException("ベースフォルダが設定されていません。");
        }

        var normalizedSupport = CaseNaming.NormalizeSupportNumber(supportNumber);
        if (!string.IsNullOrEmpty(normalizedSupport) && FindBySupport(normalizedSupport) != null)
        {
            throw new InvalidOperationException($"サポート番号 {supportNumber} の案件は既に存在します。");
        }

        var targetRoot = _basePath;
        var cleanedCategory = CaseNaming.SanitizeComponent(category);
        if (!string.IsNullOrEmpty(cleanedCategory))
        {
            targetRoot = Path.Combine(targetRoot, cleanedCategory);
            Directory.CreateDirectory(targetRoot);
        }

        var folderName = NextFolderName(targetRoot, company, supportNumber, status, createdOn);
        var folderPath = Path.Combine(targetRoot, folderName);
        Directory.CreateDirectory(folderPath);

        var record = new CaseRecord(
            company,
            supportNumber,
            status,
            createdOn,
            folderName,
            folderPath,
            CaseNaming.ToIsoTimestamp(DateTime.UtcNow),
            cleanedCategory,
            false);

        foreach (var note in NoteDefinitions.All)
        {
            NoteService.EnsureNoteFile(folderPath, note, supportNumber);
        }

        _caseIndex.Add(record);
        SaveIndex();

        if (openAfter)
        {
            OpenFolder(folderPath);
        }

        return record;
    }

    public void UpdateCaseEntry(CaseRecord updated)
    {
        var replaced = false;
        for (var i = 0; i < _caseIndex.Count; i++)
        {
            var existing = _caseIndex[i];
            if (string.Equals(existing.FolderPath, updated.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(updated.NormalizedSupport) && existing.NormalizedSupport == updated.NormalizedSupport))
            {
                _caseIndex[i] = updated;
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            _caseIndex.Add(updated);
        }

        SaveIndex();
    }

    private string NextFolderName(string root, string company, string supportNumber, string status, string createdOn)
    {
        var suffix = DateTime.Now.ToString("yyyyMMdd");
        var baseName = CaseNaming.BuildFolderName(createdOn, company, supportNumber, status, suffix);
        var candidate = Path.Combine(root, baseName);
        var counter = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(root, $"{baseName}_{counter}");
            counter += 1;
        }

        return Path.GetFileName(candidate);
    }

    private void OpenFolder(string folderPath)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(info);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private List<CaseRecord> LoadIndex()
    {
        if (string.IsNullOrEmpty(_indexPath) || !File.Exists(_indexPath))
        {
            return new List<CaseRecord>();
        }

        try
        {
            var json = File.ReadAllText(_indexPath, EncodingPolicy.Utf8NoBom);
            using var doc = JsonDocument.Parse(json);
            var records = new List<CaseRecord>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    TryAddRecord(element, records);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                TryAddRecord(doc.RootElement, records);
            }

            return records;
        }
        catch (Exception ex)
        {
            _logger.Warning($"cases-index.json の読み込みに失敗しました: {ex.Message}");
            return new List<CaseRecord>();
        }
    }

    private void SaveIndex()
    {
        if (string.IsNullOrEmpty(_indexPath))
        {
            return;
        }

        try
        {
            var payload = _caseIndex.Select(item => item.ToDto()).ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var json = JsonSerializer.Serialize(payload, options);
            File.WriteAllText(_indexPath, json, EncodingPolicy.Utf8NoBom);
        }
        catch (Exception ex)
        {
            _logger.Error($"cases-index.json の書き込みに失敗しました: {ex.Message}", ex);
        }
    }

    private void TryAddRecord(JsonElement element, List<CaseRecord> records)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<CaseRecordDto>(element.GetRawText());
            if (dto != null)
            {
                records.Add(dto.ToRecord());
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Skip corrupted index entry: {ex.Message}");
        }
    }

    private List<CaseRecord> ScanFolders()
    {
        if (string.IsNullOrEmpty(_basePath) || !Directory.Exists(_basePath))
        {
            return new List<CaseRecord>();
        }

        var stack = new Stack<string>();
        stack.Push(_basePath);
        var found = new List<CaseRecord>();

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateDirectories(current))
                {
                    var info = new DirectoryInfo(entry);
                    var record = CaseParser.ParseCaseFromDirectory(info);
                    if (record != null)
                    {
                        found.Add(record);
                    }

                    stack.Push(entry);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Directory scan skipped for {current}: {ex.Message}");
            }
        }

        return found;
    }

    private static List<CaseRecord> MergeCases(IEnumerable<CaseRecord> indexed, IEnumerable<CaseRecord> folders)
    {
        var orderedIndex = indexed.OrderByDescending(item => item.LastUpdated, StringComparer.Ordinal).ToList();
        var orderedFolder = folders.OrderByDescending(item => item.LastUpdated, StringComparer.Ordinal).ToList();
        var combined = CaseCollection.EnsureUniqueCases(orderedIndex.Concat(orderedFolder));
        combined.Sort((a, b) => string.CompareOrdinal(b.LastUpdated, a.LastUpdated));
        return combined;
    }
}
