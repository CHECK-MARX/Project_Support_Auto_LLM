using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class SearchSourceReadOnlyBindingTests
{
    [Fact]
    public void SearchSourceViewModel_TextPropertyRemainsReadOnly()
    {
        var property = typeof(SearchSourceViewModel).GetProperty(nameof(SearchSourceViewModel.Text));

        Assert.NotNull(property);
        Assert.False(property.CanWrite);
    }

    [Fact]
    public void MainWindow_SelectedEvidencePreviewTextBindingIsOneWay()
    {
        var xaml = ReadMainWindowXaml();

        Assert.Contains("Text=\"{Binding SelectedSearchResult.Text, Mode=OneWay}\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding SelectedSearchResult.Text}\"", xaml);
    }

    [Fact]
    public void MainWindow_ReadOnlySearchResultColumnsAreOneWay()
    {
        var xaml = ReadMainWindowXaml();

        Assert.Contains("Binding=\"{Binding ScoreText, Mode=OneWay}\"", xaml);
        Assert.Contains("Binding=\"{Binding SourceType, Mode=OneWay}\"", xaml);
        Assert.Contains("Binding=\"{Binding Title, Mode=OneWay}\"", xaml);
        Assert.Contains("Binding=\"{Binding Excerpt, Mode=OneWay}\"", xaml);
        Assert.Contains("Binding=\"{Binding FilePath, Mode=OneWay}\"", xaml);
        Assert.Contains("Binding=\"{Binding SendStatusText, Mode=OneWay}\"", xaml);
    }

    private static string ReadMainWindowXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("MainWindow.xaml was not found from the test output directory.");
    }
}
