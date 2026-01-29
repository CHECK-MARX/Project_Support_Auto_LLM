using System.IO;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Cases;

public class CaseParserTests
{
    [Fact]
    public void ParseCaseFromDirectory_HandlesLegacyFolder()
    {
        using var temp = new TempDirectory();
        var folderName = "202503(itoke101)調査中0322";
        var folderPath = Path.Combine(temp.Path, folderName);
        Directory.CreateDirectory(folderPath);

        var record = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(folderPath));

        Assert.NotNull(record);
        Assert.Equal("20250301", record!.CreatedOn);
        Assert.Equal("itoke", record.Company);
        Assert.Equal("00000101", record.SupportNumber);
        Assert.Equal("調査中", record.Status);
    }
}
