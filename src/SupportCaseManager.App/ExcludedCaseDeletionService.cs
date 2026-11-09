using System.IO;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Config;

namespace SupportCaseManager.App;

public enum ExcludedCaseDeletionAction { Blocked, RemoveExclusionOnly, RecycleCaseFolder }

public sealed record ExcludedCaseDeletionPlan(string FolderPath, ExcludedCaseDeletionAction Action,
    string Product, string Company, string SupportId, string Description);

public interface ICaseFolderRecycler { void Recycle(string absoluteFolderPath); }

public sealed class ExcludedCaseDeletionService(ICaseFolderRecycler? recycler = null)
{
    public ExcludedCaseDeletionPlan Prepare(string path, IEnumerable<string> excludedPaths, IEnumerable<ProductProfile> products)
    {
        var normalized = Normalize(path);
        var blocked = new ExcludedCaseDeletionPlan(normalized, ExcludedCaseDeletionAction.Blocked, "-", "", "", "除外リストに登録された保存先を確認できません。");
        if (normalized.Length == 0 || !excludedPaths.Any(item => SamePath(item, normalized))) return blocked;
        var profileList = products.ToArray();
        var product = profileList.FirstOrDefault(profile => IsWithin(profile.BasePath, normalized) || IsWithin(profile.ClosedPath, normalized));
        var rootOnly = new ExcludedCaseDeletionPlan(normalized, ExcludedCaseDeletionAction.RemoveExclusionOnly,
            product?.Name ?? "-", Path.GetFileName(normalized), "", "案件フォルダーを確認できません（除外設定のみ削除）");
        try
        {
            // Missing/stale paths and unrelated folders must never become recursive
            // deletion targets. Clearing their exclusion does not touch disk data.
            if (!Directory.Exists(normalized))
            {
                var historical = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(normalized));
                return rootOnly with { Company = historical?.Company ?? rootOnly.Company, SupportId = historical?.SupportNumber ?? "",
                    Description = "保存先なし・移動済みの可能性（除外設定のみ削除）" };
            }
            var roots = profileList.SelectMany(static profile => new[] { profile.BasePath, profile.ClosedPath }).ToArray();
            if (roots.Any(root => SamePath(root, normalized) || IsWithin(normalized, Normalize(root))))
                return rootOnly with { Description = "保存先ルートを含むため保護中（除外設定のみ削除）" };
            var record = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(normalized));
            if (record is null || string.IsNullOrWhiteSpace(record.SupportNumber) || string.IsNullOrWhiteSpace(record.Company))
                return rootOnly with { Description = "案件形式ではないフォルダー（除外設定のみ削除）" };
            if (product is null || !CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(normalized, roots, out var safe))
                return rootOnly with { Company = record.Company, SupportId = record.SupportNumber,
                    Description = "保存先の範囲外・リンク先等（除外設定のみ削除）" };
            if (!new DriveInfo(Path.GetPathRoot(safe)!).DriveType.Equals(DriveType.Fixed))
                return rootOnly with { Company = record.Company, SupportId = record.SupportNumber,
                    Description = "ごみ箱への移動対象外のドライブ（除外設定のみ削除）" };
            return new(safe, ExcludedCaseDeletionAction.RecycleCaseFolder, product.Name, record.Company,
                record.SupportNumber, "案件フォルダーあり（削除時はごみ箱へ移動）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return rootOnly with { Description = "保存先を確認できません（除外設定のみ削除）" }; }
    }

    public IReadOnlyList<string> Execute(ExcludedCaseDeletionPlan confirmed, IEnumerable<string> excludedPaths, IEnumerable<ProductProfile> products)
    {
        var current = excludedPaths.ToArray();
        var checkedAgain = Prepare(confirmed.FolderPath, current, products);
        if (confirmed.Action == ExcludedCaseDeletionAction.Blocked || checkedAgain != confirmed)
            throw new InvalidOperationException("確認後に保存先または除外設定が変わりました。削除していません。再読み込みしてください。");
        if (confirmed.Action == ExcludedCaseDeletionAction.RecycleCaseFolder)
        {
            (recycler ?? new WindowsCaseFolderRecycler()).Recycle(checkedAgain.FolderPath);
            if (Directory.Exists(checkedAgain.FolderPath))
                throw new IOException("フォルダーが残っているため、除外設定は保持しました。ごみ箱と保存先を確認してください。");
        }
        return current.Where(path => !SamePath(path, confirmed.FolderPath)).ToArray();
    }

    internal static bool SamePath(string? left, string? right) => Normalize(left) is { Length: > 0 } path
        && path.Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string? root, string path)
    {
        var normalizedRoot = Normalize(root);
        return normalizedRoot.Length > 0 && path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return string.Empty;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return string.Empty; }
    }
}
