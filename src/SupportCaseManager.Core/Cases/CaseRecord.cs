using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Core.Cases;

public sealed class CaseRecord
{
    public string Company { get; set; }
    public string SupportNumber { get; set; }
    public string Status { get; set; }
    public string CreatedOn { get; set; }
    public string FolderName { get; set; }
    public string FolderPath { get; set; }
    public string LastUpdated { get; set; }
    public string Category { get; set; }
    public bool IsFromFolder { get; set; }
    public string NormalizedSupport { get; private set; }

    public CaseRecord(
        string company,
        string supportNumber,
        string status,
        string createdOn,
        string folderName,
        string folderPath,
        string lastUpdated,
        string category = "",
        bool isFromFolder = false)
    {
        Company = company ?? string.Empty;
        SupportNumber = supportNumber ?? string.Empty;
        Status = status ?? string.Empty;
        CreatedOn = createdOn ?? string.Empty;
        FolderName = folderName ?? string.Empty;
        FolderPath = folderPath ?? string.Empty;
        LastUpdated = lastUpdated ?? string.Empty;
        Category = category ?? string.Empty;
        IsFromFolder = isFromFolder;
        NormalizedSupport = string.Empty;
        Normalize();
    }

    public void Normalize()
    {
        Company = Company.Trim();
        SupportNumber = CaseNaming.FormatSupportNumber(SupportNumber);
        Status = CaseNaming.NormalizeStatus(Status ?? string.Empty);
        CreatedOn = CaseNaming.EnsureDateString(CreatedOn);
        FolderPath = FolderPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(FolderName))
        {
            FolderName = Path.GetFileName(FolderPath);
        }

        LastUpdated = CaseNaming.ToIsoTimestamp(LastUpdated);
        Category = Category.Trim();
        NormalizedSupport = CaseNaming.NormalizeSupportNumber(SupportNumber);
    }

    public string DisplayText()
    {
        var displayStatus = string.IsNullOrWhiteSpace(Status) ? "未設定" : Status;
        var support = string.IsNullOrWhiteSpace(SupportNumber) ? "-" : SupportNumber;
        return $"{CreatedOn} ({Company} {support}) {displayStatus}";
    }

    public CaseRecord CloneWith(bool? isFromFolder = null)
    {
        return new CaseRecord(
            Company,
            SupportNumber,
            Status,
            CreatedOn,
            FolderName,
            FolderPath,
            LastUpdated,
            Category,
            isFromFolder ?? IsFromFolder);
    }

    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["company"] = Company,
            ["support_number"] = SupportNumber,
            ["status"] = Status,
            ["created_on"] = CreatedOn,
            ["folder_name"] = FolderName,
            ["folder_path"] = FolderPath,
            ["last_updated"] = LastUpdated,
            ["category"] = Category,
            ["is_from_folder"] = IsFromFolder,
        };
    }

    public CaseRecordDto ToDto()
    {
        return new CaseRecordDto
        {
            Company = Company,
            SupportNumber = SupportNumber,
            Status = Status,
            CreatedOn = CreatedOn,
            FolderName = FolderName,
            FolderPath = FolderPath,
            LastUpdated = LastUpdated,
            Category = Category,
            IsFromFolder = IsFromFolder,
        };
    }
}

public sealed class CaseRecordDto
{
    [JsonPropertyName("company")]
    public string Company { get; set; } = string.Empty;

    [JsonPropertyName("support_number")]
    public string SupportNumber { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("created_on")]
    public string CreatedOn { get; set; } = string.Empty;

    [JsonPropertyName("folder_name")]
    public string FolderName { get; set; } = string.Empty;

    [JsonPropertyName("folder_path")]
    public string FolderPath { get; set; } = string.Empty;

    [JsonPropertyName("last_updated")]
    public string LastUpdated { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("is_from_folder")]
    public bool IsFromFolder { get; set; }

    public CaseRecord ToRecord()
    {
        return new CaseRecord(
            Company,
            SupportNumber,
            Status,
            string.IsNullOrEmpty(CreatedOn) ? DateTime.UtcNow.ToString("yyyyMMdd") : CreatedOn,
            FolderName,
            FolderPath,
            string.IsNullOrEmpty(LastUpdated) ? CaseNaming.ToIsoTimestamp(DateTime.UtcNow) : LastUpdated,
            Category,
            IsFromFolder);
    }
}
