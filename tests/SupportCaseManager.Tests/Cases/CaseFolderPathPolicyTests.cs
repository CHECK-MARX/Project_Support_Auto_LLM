using SupportCaseManager.Core.Cases;
using SupportCaseManager.Tests.Helpers;

namespace SupportCaseManager.Tests.Cases;

public sealed class CaseFolderPathPolicyTests
{
    [Fact]
    public void AcceptsExistingChildAndNormalizesPath()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "cases", "SUP-001")).FullName;

        var accepted = CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            Path.Combine(root, "cases", "..", "cases", "SUP-001"),
            [root],
            out var normalized);

        Assert.True(accepted);
        Assert.Equal(Path.GetFullPath(child), normalized);
    }

    [Fact]
    public void RejectsRootItselfSiblingPrefixAndMissingFolder()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "safe")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(temp.Path, "safe2", "SUP-002")).FullName;

        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(root, [root], out _));
        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(sibling, [root], out _));
        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            Path.Combine(root, "missing"), [root], out _));
    }

    [Fact]
    public void AcceptsAnyConfiguredRootButRejectsTraversalToAnotherRoot()
    {
        using var temp = new TempDirectory();
        var activeRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "active")).FullName;
        var closedRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "closed")).FullName;
        var closedCase = Directory.CreateDirectory(Path.Combine(closedRoot, "2026", "SUP-003")).FullName;

        Assert.True(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            closedCase, [activeRoot, closedRoot], out _));
        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            Path.Combine(activeRoot, "..", "outside"), [activeRoot], out _));
    }

    [Fact]
    public void AcceptsTrailingSeparatorAndLongChildPath()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var child = root;
        for (var index = 0; index < 12; index++)
        {
            child = Path.Combine(child, $"case-segment-{index:D2}");
        }

        Directory.CreateDirectory(child);

        Assert.True(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            child + Path.DirectorySeparatorChar, [root], out var normalized));
        Assert.Equal(Path.GetFullPath(child), normalized);
    }

    [Fact]
    public void RejectsAbsoluteUncDriveAndAlternateDataStreamPathsOutsideRoot()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "SUP-005")).FullName;

        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            @"\\server\share\SUP-005", [root], out _));
        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            @"Z:\SUP-005", [root], out _));
        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(
            child + ":metadata", [root], out _));
    }

    [Fact]
    public void RejectsDirectoryReachedThroughSymbolicLinkWhenSupported()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside", "SUP-004")).FullName;
        var link = Path.Combine(root, "linked-case");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(CaseFolderPathPolicy.TryNormalizeExistingFolderWithinRoots(link, [root], out _));
    }

    [Fact]
    public void AcceptsNonExistingDestinationBelowRoot()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var destination = Path.Combine(root, "category", "SUP-006");

        Assert.True(CaseFolderPathPolicy.TryNormalizeDestinationWithinRoots(
            destination, [root], out var normalized));
        Assert.Equal(Path.GetFullPath(destination), normalized);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void RejectsDestinationTraversalSiblingRootAndAlternateDataStream()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "safe")).FullName;

        Assert.False(CaseFolderPathPolicy.TryNormalizeDestinationWithinRoots(root, [root], out _));
        Assert.False(CaseFolderPathPolicy.TryNormalizeDestinationWithinRoots(
            Path.Combine(root, "..", "safe2", "SUP-007"), [root], out _));
        Assert.False(CaseFolderPathPolicy.TryNormalizeDestinationWithinRoots(
            Path.Combine(root, "SUP-007") + ":metadata", [root], out _));
    }

    [Fact]
    public void RejectsDestinationBelowSymbolicLinkWhenSupported()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        var link = Path.Combine(root, "linked-category");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(CaseFolderPathPolicy.TryNormalizeDestinationWithinRoots(
            Path.Combine(link, "SUP-008"), [root], out _));
    }
}
