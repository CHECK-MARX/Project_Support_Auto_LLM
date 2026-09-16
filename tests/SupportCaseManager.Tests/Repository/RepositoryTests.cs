using System.Text.Json;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Repository;
using SupportCaseManager.Core.Logging;
using SupportCaseManager.Core.Notes;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Repository;

public class RepositoryTests
{
    [Fact]
    public void SetBasePathRejectsAlternateDataStream()
    {
        using var temp = new TempDirectory();
        var repository = new CaseRepository(NullLogger.Instance);

        Assert.Throws<ArgumentException>(() => repository.SetBasePath(temp.Path + ":metadata"));
        Assert.Null(repository.BasePath);
    }

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
    public void AllCases_RemovesEntriesOutsideBasePath()
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

        Assert.Empty(cases);

        var json = File.ReadAllText(indexPath, SupportCaseManager.Core.Compatibility.EncodingPolicy.Utf8NoBom);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Empty(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public void AllCases_PrefersFilesystemRecordOverStaleIndexPath()
    {
        using var temp = new TempDirectory();
        var basePath = temp.Path;
        var indexPath = Path.Combine(basePath, "cases-index.json");
        var currentName = "20250102(ABC_00000001)調査中_20250103";
        var currentPath = Path.Combine(basePath, currentName);
        Directory.CreateDirectory(currentPath);
        var stalePath = Path.Combine(basePath, "2025", currentName);
        var sample = $$"""
[
  {
    "company": "ABC",
    "support_number": "00000001",
    "status": "調査中",
    "created_on": "20250102",
    "folder_name": "{{currentName}}",
    "folder_path": "{{stalePath.Replace("\\", "\\\\")}}",
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
        Assert.Equal(currentPath, cases[0].FolderPath);
        Assert.True(File.Exists(indexPath));

        var json = File.ReadAllText(indexPath, SupportCaseManager.Core.Compatibility.EncodingPolicy.Utf8NoBom);
        using var doc = JsonDocument.Parse(json);
        var saved = doc.RootElement.EnumerateArray().Single();
        Assert.Equal(currentPath, saved.GetProperty("folder_path").GetString());
    }

    [Fact]
    public void AllCases_PreservesGptRegistrationFromIndexWhenFolderIsScanned()
    {
        using var temp = new TempDirectory();
        var folderName = "20260916(ABC_00018303)調査中_20260916";
        var folderPath = Path.Combine(temp.Path, folderName);
        Directory.CreateDirectory(folderPath);
        var repository = new CaseRepository(NullLogger.Instance);
        repository.SetBasePath(temp.Path);
        var indexed = new CaseRecord(
            "ABC", "00018303", "調査中", "20260916", folderName, folderPath, "2026-09-16T00:00:00Z")
        {
            GptRegistration = new GptCaseRegistration
            {
                SupportId = "00018303",
                Product = "Checkmarx",
                TargetGptKey = "checkmarx",
                TargetGptDisplayName = "Vulnerability Scanner Assistant",
                ConversationUrl = "https://chatgpt.com/c/existing",
                RegisteredAt = "2026-09-16T12:00:00+09:00",
                LinkMode = GptRegistrationLinkModes.CreatedByApp,
                RegistrationState = GptRegistrationStates.Registered,
            },
        };
        repository.UpdateCaseEntry(indexed);

        var loaded = Assert.Single(repository.AllCases());

        Assert.True(loaded.GptRegistration.IsRegistered);
        Assert.Equal("https://chatgpt.com/c/existing", loaded.GptRegistration.ConversationUrl);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "cases-index.json")));
        Assert.Equal(
            "REGISTERED",
            document.RootElement[0].GetProperty("gpt_registration").GetProperty("registration_state").GetString());
    }

    [Fact]
    public void CreateCase_DoesNotRegisterGptAutomatically()
    {
        using var temp = new TempDirectory();
        var repository = new CaseRepository(NullLogger.Instance);
        repository.SetBasePath(temp.Path);

        var record = repository.CreateCase("ABC", "00018303", "調査中", "20260916");

        Assert.Equal(GptRegistrationStates.Unregistered, record.GptRegistration.RegistrationState);
        Assert.Empty(record.GptRegistration.ConversationUrl);
    }
}
