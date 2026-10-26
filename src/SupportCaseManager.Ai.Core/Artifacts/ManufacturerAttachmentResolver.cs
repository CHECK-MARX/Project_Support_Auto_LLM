using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ManufacturerAttachmentResolution(
    IReadOnlyList<CodexCaseFileInfo> Files, IReadOnlyList<string> Warnings);

/// <summary>Uses the current inquiry bundle, never the entire case's historical attachments.</summary>
public static class ManufacturerAttachmentResolver
{
    public static ManufacturerAttachmentResolution Resolve(string caseFolder, string? inquiryPath,
        string inquiryText, string? translationSource, string? translationOutput,
        IEnumerable<CodexCaseFileInfo> files)
    {
        var policy = new CaseArtifactPathPolicy();
        var available = files.Where(file => !file.FileName.StartsWith("~$", StringComparison.Ordinal))
            .Where(file => file.Size > 0 && file.Kind != CodexCaseFileKind.GptHandoff)
            .Where(file => !IsCaseNote(file.FileName))
            .Where(file => IsSafe(file.FullPath)).ToArray();
        var references = Regex.Matches(inquiryText,
                @"[\p{L}\p{N}_][\p{L}\p{N}_().~-]*\.(?:pdf|zip|7z|xml|xlsx|docx|pptx|csv|log|txt|png|jpg)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var anchor = IsCustomerBundle(translationSource) ? translationSource : inquiryPath;
        var directory = string.IsNullOrEmpty(anchor) ? null : Path.GetDirectoryName(anchor);
        var hasBundle = IsCustomerBundle(anchor) && !string.IsNullOrEmpty(directory)
            && !string.Equals(Path.GetFullPath(directory), Path.GetFullPath(caseFolder), StringComparison.OrdinalIgnoreCase);
        var candidates = available.Where(file =>
            (hasBundle && file.FullPath.StartsWith(directory!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            || (references.Contains(file.FileName, StringComparer.OrdinalIgnoreCase)
                && (IsCustomerBundle(file.FullPath) || Path.GetDirectoryName(file.FullPath) == caseFolder)))
            .Where(file => !IsTranslated(file.FileName)
                || string.Equals(file.FullPath, translationOutput, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrEmpty(translationOutput) && IsSafe(translationOutput))
        {
            candidates.RemoveAll(file => string.Equals(file.FullPath, translationSource, StringComparison.OrdinalIgnoreCase));
            candidates.Add(available.FirstOrDefault(file => string.Equals(file.FullPath, translationOutput, StringComparison.OrdinalIgnoreCase))
                ?? new CodexCaseFileInfo { FullPath = translationOutput, FileName = Path.GetFileName(translationOutput),
                    RelativePath = Path.GetRelativePath(caseFolder, translationOutput), Size = new FileInfo(translationOutput).Length });
        }
        return new(candidates.DistinctBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            references.Where(name => !available.Any(file => string.Equals(file.FileName, name, StringComparison.OrdinalIgnoreCase)))
                .Select(name => $"顧客記載の添付名に一致するファイルがありません: {name}。実ファイルとの対応を確認してください。")
                .ToArray());

        bool IsSafe(string path)
        {
            try { policy.NormalizeSelectedSourceFile(caseFolder, path); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { return false; }
        }
    }

    public static bool IsCustomerBundle(string? path) => !string.IsNullOrEmpty(path)
        && (path.Contains("お客様ご相談内容", StringComparison.OrdinalIgnoreCase)
            || path.Contains("追加問い合わせ", StringComparison.OrdinalIgnoreCase)
            || path.Contains("customerinquiry", StringComparison.OrdinalIgnoreCase)
            || path.Contains("additional_inquiry", StringComparison.OrdinalIgnoreCase));

    private static bool IsCaseNote(string name) => name.StartsWith("お客様ご相談内容_", StringComparison.Ordinal)
        || name.StartsWith("お客様への返信案_", StringComparison.Ordinal)
        || name.StartsWith("メーカー連携内容_", StringComparison.Ordinal);

    private static bool IsTranslated(string name) => Regex.IsMatch(name, @"_EN(?:_\d+)?\.", RegexOptions.IgnoreCase)
        || name.StartsWith("Translated_", StringComparison.OrdinalIgnoreCase);
}
