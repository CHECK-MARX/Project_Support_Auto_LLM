using System.Text.Json;
using SupportCaseManager.Core.Repository;
using SupportCaseManager.Core.Logging;
using SupportCaseManager.Core.Notes;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Repository;

public class RepositoryTests
{
    [Fact]
    public void CreateCase_CreatesFolderAndNotes()
    {
        using var temp = new TempDirectory();
        var repository = new CaseRepository(NullLogger.Instance);
        repository.SetBasePath(temp.Path);

        var record = repository.CreateCase(
            "MMM",
            "00001234",
            "調査中",
            "20251013",
            openAfter: false);

        Assert.True(Directory.Exists(record.FolderPath));
        foreach (var definition in NoteDefinitions.All)
        {
            var notePath = Path.Combine(record.FolderPath, definition.FileName(record.SupportNumber));
            Assert.True(File.Exists(notePath));
        }
    }

    [Fact]
    public void AllCases_PreservesIndexData()
    {
        using var temp = new TempDirectory();
        var basePath = temp.Path;
        var indexPath = Path.Combine(basePath, "cases-index.json");
        var sample = """
[
  {
    "company": "ABC",
    "support_number": "00000001",
    "status": "調査中",
    "created_on": "20250102",
    "folder_name": "20250102(ABC_00000001)調査中_20250103",
    "folder_path": "C:\\Cases\\20250102(ABC_00000001)調査中_20250103",
    "last_updated": "2025-01-03T12:34:56.123456",
    "category": "",
    "is_from_folder": false
  }
]
""";
        File.WriteAllText(indexPath, sample, SupportCaseManager.Core.Compatibility.EncodingPolicy.Utf8NoBom);

        var repository = new CaseRepository(NullLogger.Instance);
        repository.SetBasePath(basePath);
        var cases = repository.AllCases();

        Assert.Single(cases);
        Assert.Equal("ABC", cases[0].Company);
        Assert.Equal("00000001", cases[0].SupportNumber);

        var json = File.ReadAllText(indexPath, SupportCaseManager.Core.Compatibility.EncodingPolicy.Utf8NoBom);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }
}
