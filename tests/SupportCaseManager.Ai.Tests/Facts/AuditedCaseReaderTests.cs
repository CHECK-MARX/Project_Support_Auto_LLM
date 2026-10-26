using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Facts;

public sealed class AuditedCaseReaderTests
{
    [Fact]
    public void RestoresOnlyUnchangedAuditedQuestionAndVerifiedVisualFinding()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "case"); var audit = Path.Combine(root, "audit");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(audit);
        try
        {
            var question = Path.Combine(folder, "question.txt");
            File.WriteAllText(question, "Windows 11 Pro 24H2でセットアップ手順とツールの入手先を教えてください。", new UTF8Encoding(false));
            var image = Path.Combine(folder, "image.png"); File.WriteAllBytes(image, [1,2,3]);
            var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(question)));
            File.WriteAllText(Path.Combine(audit, "source-notes-mail-private.json"), JsonSerializer.Serialize(new
            {
                cases = new Dictionary<string, object> { ["case-a"] = new { notes = new[]
                    { new { category = "question", path = question, sha256 = sha, text = File.ReadAllText(question) } } } },
            }));
            var material = Path.Combine(root, "material.json");
            File.WriteAllText(material, JsonSerializer.Serialize(new { cases = new Dictionary<string, object>
            {
                ["case-a"] = new { files = new[] { new { status = "image_visual_reviewed", bytes = 3,
                    sha256prefix = Convert.ToHexStringLower(SHA256.HashData(new byte[]{1,2,3}))[..12], extension = ".png",
                    visualFinding = "添付画面にはDashboardサービスの設定が表示されている。" } } },
            }}));
            var result = CurrentCaseFactReader.ReadAudited(folder, "case-a", "セットアップ", audit, material, new SafetyRedactionService());
            Assert.Contains("24H2", result.Inquiry);
            Assert.Equal("CurrentCase", Assert.Single(result.Facts).SourceType);
            File.WriteAllText(question, "modified"); File.WriteAllBytes(image, [9,9,9]);
            var changed = CurrentCaseFactReader.ReadAudited(folder, "case-a", "セットアップ", audit, material, new SafetyRedactionService());
            Assert.Equal("セットアップ", changed.Inquiry);
            Assert.Empty(changed.Facts);
        }
        finally { Directory.Delete(root, true); }
    }
}
