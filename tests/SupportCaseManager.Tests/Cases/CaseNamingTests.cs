using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.Tests.Cases;

public class CaseNamingTests
{
    [Fact]
    public void BuildFolderName_FormatsExpectedPattern()
    {
        var result = CaseNaming.BuildFolderName("20251013", "MMM", "00001234", "調査中", "20251013");
        Assert.Equal("20251013(MMM_00001234)調査中_20251013", result);
    }

    [Fact]
    public void SplitStatusWithLegacy_DetectsTrailingDigits()
    {
        var (status, stamp) = CaseNaming.SplitStatusWithLegacy("調査中0322");
        Assert.Equal("調査中", status);
        Assert.Equal("0322", stamp);
    }

    [Fact]
    public void NormalizeSupportNumber_PadsDigits()
    {
        var result = CaseNaming.NormalizeSupportNumber("1234");
        Assert.Equal("00001234", result);
    }
}
