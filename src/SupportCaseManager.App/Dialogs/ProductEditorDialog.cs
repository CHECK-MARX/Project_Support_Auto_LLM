using System;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace SupportCaseManager.App.Dialogs;

public sealed class ProductEditorDialog : Window
{
    private readonly System.Windows.Controls.TextBox _nameBox;
    private readonly System.Windows.Controls.TextBox _basePathBox;

    public string ProductName => _nameBox.Text.Trim();
    public string BasePath => _basePathBox.Text.Trim();

    public ProductEditorDialog(string title, string name, string basePath)
    {
        Title = title;
        Width = 520;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameLabel = new TextBlock { Text = "プロダクト名", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(nameLabel, 0);
        Grid.SetColumn(nameLabel, 0);
        root.Children.Add(nameLabel);

        _nameBox = new System.Windows.Controls.TextBox { Text = name, Margin = new Thickness(8, 0, 0, 8) };
        Grid.SetRow(_nameBox, 0);
        Grid.SetColumn(_nameBox, 1);
        Grid.SetColumnSpan(_nameBox, 2);
        root.Children.Add(_nameBox);

        var pathLabel = new TextBlock { Text = "ベースフォルダ", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(pathLabel, 1);
        Grid.SetColumn(pathLabel, 0);
        root.Children.Add(pathLabel);

        _basePathBox = new System.Windows.Controls.TextBox { Text = basePath, Margin = new Thickness(8, 0, 8, 8) };
        Grid.SetRow(_basePathBox, 1);
        Grid.SetColumn(_basePathBox, 1);
        root.Children.Add(_basePathBox);

        var browseButton = new System.Windows.Controls.Button { Content = "参照...", Margin = new Thickness(0, 0, 0, 8) };
        browseButton.Click += (_, _) => BrowseFolder();
        Grid.SetRow(browseButton, 1);
        Grid.SetColumn(browseButton, 2);
        root.Children.Add(browseButton);

        var buttonRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 80 };
        var cancelButton = new System.Windows.Controls.Button { Content = "キャンセル", Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        okButton.Click += (_, _) => Accept();
        cancelButton.Click += (_, _) => DialogResult = false;
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        Grid.SetRow(buttonRow, 3);
        Grid.SetColumn(buttonRow, 0);
        Grid.SetColumnSpan(buttonRow, 3);
        root.Children.Add(buttonRow);

        Content = root;
    }

    private void BrowseFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "ベースフォルダを選択",
            UseDescriptionForTitle = true,
            SelectedPath = _basePathBox.Text,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _basePathBox.Text = dialog.SelectedPath;
        }
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(ProductName))
        {
            MessageBox.Show(this, "プロダクト名を入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(BasePath))
        {
            MessageBox.Show(this, "ベースフォルダを入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
