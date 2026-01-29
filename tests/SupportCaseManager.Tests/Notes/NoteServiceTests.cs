using System.Text;
using SupportCaseManager.Core.Notes;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Notes;

public class NoteServiceTests
{
    [Fact]
    public void AppendNote_WritesUtf8WithoutBomAndCrLf()
    {
        using var temp = new TempDirectory();
        var folder = temp.Path;
        var definition = NoteDefinitions.All[0];

        var path = NoteService.AppendNote(folder, definition, "00001234", "調査中", "テスト");

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("*****追記部_", text);
        Assert.Contains("(調査中)******", text);
        Assert.Contains("--------------------------------------------------", text);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                Assert.True(i > 0 && text[i - 1] == '\r');
            }
        }
    }
}
