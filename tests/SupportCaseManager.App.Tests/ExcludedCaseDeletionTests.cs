using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SupportCaseManager.App.Tests.Helpers;
using SupportCaseManager.Core.Config;

namespace SupportCaseManager.App.Tests;

public sealed class ExcludedCaseDeletionTests
{
    [Fact]
    public void ExcludedCaseCanBeRecycledButOtherCasesAndExclusionsRemain()
    {
        using var temp = new TempDirectory();
        var products = Profiles(temp.Path);
        var test = NewCase(products[0].BasePath, "00000123");
        var other = NewCase(products[0].BasePath, "00000456");
        File.WriteAllText(Path.Combine(test, "note.txt"), "test note");
        var recycle = new FakeRecycler(Path.Combine(temp.Path, "recycled"));
        var service = new ExcludedCaseDeletionService(recycle);
        var excluded = new[] { test, other };
        var plan = service.Prepare(test, excluded, products);
        Assert.Equal(ExcludedCaseDeletionAction.RecycleCaseFolder, plan.Action);
        Assert.Equal("00000123", plan.SupportId);
        var remaining = service.Execute(plan, excluded, products);
        Assert.Equal([other], remaining);
        Assert.False(Directory.Exists(test));
        Assert.True(Directory.Exists(other));
        Assert.Equal("test note", File.ReadAllText(Path.Combine(temp.Path, "recycled", "note.txt")));
        Assert.Equal(1, recycle.Calls);
    }

    [Fact]
    public void MissingOldCasePathOnlyRemovesExactExclusionAndNeverFollowsSameIdAtClosedLocation()
    {
        using var temp = new TempDirectory();
        var products = Profiles(temp.Path);
        var missing = Path.Combine(products[0].BasePath, "20261109(Test_00000123)調査中_20261109");
        var closed = NewCase(products[0].ClosedPath, "00000123");
        var recycler = new FakeRecycler(Path.Combine(temp.Path, "recycled"));
        var service = new ExcludedCaseDeletionService(recycler);
        var plan = service.Prepare(missing, [missing], products);
        Assert.Equal(ExcludedCaseDeletionAction.RemoveExclusionOnly, plan.Action);
        Assert.Equal("00000123", plan.SupportId);
        Assert.Equal("Test", plan.Company);
        Assert.Contains("保存先なし", plan.Description);
        Assert.Empty(service.Execute(plan, [missing], products));
        Assert.True(Directory.Exists(closed));
        Assert.Equal(0, recycler.Calls);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("noncase")]
    [InlineData("outside")]
    [InlineData("prefix-sibling")]
    [InlineData("nested-configured-root")]
    public void UnrelatedFoldersRootsAndOutOfScopeCasesOnlyRemoveSettings(string kind)
    {
        using var temp = new TempDirectory();
        var products = Profiles(temp.Path);
        var target = kind switch
        {
            "root" => products[0].BasePath,
            "noncase" => Directory.CreateDirectory(Path.Combine(products[0].BasePath, "資料")).FullName,
            "outside" => NewCase(Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName, "00000123"),
            "prefix-sibling" => NewCase(Directory.CreateDirectory(products[0].BasePath + "-other").FullName, "00000123"),
            _ => NewCase(products[0].BasePath, "00000123")
        };
        if (kind == "nested-configured-root") products.Add(new() { Name = "Other", BasePath = Directory.CreateDirectory(Path.Combine(target, "other-product")).FullName });
        var recycler = new FakeRecycler(Path.Combine(temp.Path, "recycled"));
        var service = new ExcludedCaseDeletionService(recycler);
        var plan = service.Prepare(target, [target], products);
        Assert.Equal(ExcludedCaseDeletionAction.RemoveExclusionOnly, plan.Action);
        Assert.Empty(service.Execute(plan, [target], products));
        Assert.True(Directory.Exists(target));
        Assert.Equal(0, recycler.Calls);
    }

    [Fact]
    public void NonExcludedPathAndChangesAfterConfirmationCannotRecycleAnything()
    {
        using var temp = new TempDirectory();
        var products = Profiles(temp.Path);
        var path = NewCase(products[0].BasePath, "00000123");
        var recycler = new FakeRecycler(Path.Combine(temp.Path, "recycled"));
        var service = new ExcludedCaseDeletionService(recycler);
        Assert.Equal(ExcludedCaseDeletionAction.Blocked, service.Prepare(path, [], products).Action);
        var plan = service.Prepare(path, [path], products);
        Assert.Throws<InvalidOperationException>(() => service.Execute(plan, [], products));
        Assert.Throws<InvalidOperationException>(() => service.Execute(plan, [path], []));
        Assert.Throws<InvalidOperationException>(() => service.Execute(plan with { SupportId = "00000456" }, [path], products));
        Directory.Move(path, path + "-moved");
        Assert.Throws<InvalidOperationException>(() => service.Execute(plan, [path], products));
        Assert.Equal(0, recycler.Calls);
    }

    [Fact]
    public void FailedOrCancelledRecycleNeverReturnsASettingRemoval()
    {
        using var temp = new TempDirectory();
        var products = Profiles(temp.Path);
        var path = NewCase(products[0].BasePath, "00000123");
        foreach (var error in new Exception[] { new IOException("locked"), new OperationCanceledException() })
        {
            var service = new ExcludedCaseDeletionService(new ThrowingRecycler(error));
            var plan = service.Prepare(path, [path], products);
            Assert.Throws(error.GetType(), () => service.Execute(plan, [path], products));
            Assert.True(Directory.Exists(path));
        }
    }

    [Fact]
    public void CancelledNativeOperationThatLeavesFolderCannotBeReportedAsDeleted()
    {
        using var temp = new TempDirectory();
        var products = Profiles(temp.Path);
        var path = NewCase(products[0].BasePath, "00000123");
        var service = new ExcludedCaseDeletionService(new NoopRecycler());
        Assert.Throws<IOException>(() => service.Execute(service.Prepare(path, [path], products), [path], products));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void DeleteButtonFollowsRestoreAndMissingRowsHaveVisiblePathAndReason()
    {
        var xaml = XDocument.Load(FindMainWindowPath());
        var grid = xaml.Descendants().Single(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ExcludedCaseGrid"));
        var buttons = grid.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();
        Assert.Equal(new[] { "戻す", "削除" }, buttons.Select(element => element.Attribute("Content")?.Value));
        Assert.Equal("OnExcludedCaseDelete", buttons[1].Attribute("Click")?.Value);
        Assert.Contains(grid.Descendants(), element => element.Attribute("Binding")?.Value == "{Binding DeletionDescription}");
        Assert.Contains(grid.Descendants(), element => element.Attribute("Binding")?.Value == "{Binding FolderPath}");
    }

    private static List<ProductProfile> Profiles(string root) => [new()
    {
        Name = "Test product", BasePath = Directory.CreateDirectory(Path.Combine(root, "cases")).FullName,
        ClosedPath = Directory.CreateDirectory(Path.Combine(root, "closed")).FullName
    }];
    private static string NewCase(string root, string id) => Directory.CreateDirectory(Path.Combine(root, $"20261109(Test_{id})調査中_20261109")).FullName;
    private sealed class FakeRecycler(string destination) : ICaseFolderRecycler
    {
        public int Calls { get; private set; }
        public void Recycle(string path) { Calls++; Directory.Move(path, destination); }
    }
    private sealed class ThrowingRecycler(Exception error) : ICaseFolderRecycler { public void Recycle(string path) => throw error; }
    private sealed class NoopRecycler : ICaseFolderRecycler { public void Recycle(string path) { } }
    private static string FindMainWindowPath([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "SupportCaseManager.App", "MainWindow.xaml"));
}
