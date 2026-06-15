using System.Globalization;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.Ai.Core.Cases;

public sealed class CaseContextBuilder : ICaseContextBuilder
{
    private readonly INoteSnapshotReader noteSnapshotReader;

    public CaseContextBuilder(INoteSnapshotReader noteSnapshotReader)
    {
        this.noteSnapshotReader = noteSnapshotReader ?? throw new ArgumentNullException(nameof(noteSnapshotReader));
    }

    public async Task<CaseContext> BuildFromCaseFolderAsync(
        string caseFolderPath,
        string? productName = null,
        string? baseFolder = null,
        string? closeFolder = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseFolderPath))
        {
            throw new ArgumentException("Case folder path is required.", nameof(caseFolderPath));
        }

        var directory = new DirectoryInfo(caseFolderPath);
        var record = CaseParser.ParseCaseFromDirectory(directory);
        var notes = await noteSnapshotReader.ReadAllAsync(caseFolderPath, cancellationToken);

        return new CaseContext
        {
            Source = "SupportCaseManager.Ai.Core",
            ProductName = productName,
            BaseFolder = baseFolder,
            CloseFolder = closeFolder,
            CaseFolderPath = directory.FullName,
            CompanyName = record?.Company,
            SupportNumber = record?.SupportNumber,
            Status = record?.Status,
            ReceptionDate = ParseReceptionDate(record?.CreatedOn),
            Notes = notes,
        };
    }

    private static DateOnly? ParseReceptionDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParseExact(
            value,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }
}
