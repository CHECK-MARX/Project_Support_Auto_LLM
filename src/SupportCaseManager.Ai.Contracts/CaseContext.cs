using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class CaseContext
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; init; }

    [JsonPropertyName("baseFolder")]
    public string? BaseFolder { get; init; }

    [JsonPropertyName("closeFolder")]
    public string? CloseFolder { get; init; }

    [JsonPropertyName("caseFolderPath")]
    public string? CaseFolderPath { get; init; }

    [JsonPropertyName("companyName")]
    public string? CompanyName { get; init; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; init; }

    [JsonPropertyName("supportNumber")]
    public string? SupportNumber { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("receptionDate")]
    public DateOnly? ReceptionDate { get; init; }

    [JsonPropertyName("selectedText")]
    public string? SelectedText { get; init; }

    [JsonPropertyName("notes")]
    public IReadOnlyList<NoteSnapshot> Notes { get; init; } = [];
}
