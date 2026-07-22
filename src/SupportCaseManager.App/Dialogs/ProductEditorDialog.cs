using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Config;
using MessageBox = System.Windows.MessageBox;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace SupportCaseManager.App.Dialogs;

public sealed class ProductEditorDialog : Window
{
    private readonly WpfTextBox _nameBox;
    private readonly WpfTextBox _aliasesBox;
    private readonly WpfTextBox _basePathBox;
    private readonly WpfTextBox _closedPathBox;
    private readonly WpfTextBox _promptPathBox;
    private readonly WpfCheckBox _enabledBox;
    private readonly WpfTextBox _sortOrderBox;

    public Guid ProductId { get; }
    public string ProductName => _nameBox.Text.Trim();
    public IReadOnlyList<string> Aliases => SplitAliases(_aliasesBox.Text);
    public string BasePath => _basePathBox.Text.Trim();
    public string ClosedPath => _closedPathBox.Text.Trim();
    public string ProductPromptFilePath => _promptPathBox.Text.Trim();
    public bool IsProductEnabled => _enabledBox.IsChecked == true;
    public int SortOrder => int.TryParse(_sortOrderBox.Text.Trim(), out var value) ? Math.Max(0, value) : 0;

    public ProductEditorDialog(string title, ProductDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        ProductId = definition.Id == Guid.Empty ? Guid.NewGuid() : definition.Id;
        Title = title;
        Width = 760;
        Height = 470;
        MinWidth = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var row = 0; row < 8; row++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = row == 7 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

        _nameBox = AddTextRow(root, 0, "製品表示名", definition.DisplayName);
        _aliasesBox = AddTextRow(root, 1, "エイリアス", string.Join(", ", definition.Aliases ?? []));
        _basePathBox = AddPathRow(root, 2, "ベースフォルダ", definition.BaseFolder, target => BrowseFolder(target, "ベースフォルダを選択"));
        _closedPathBox = AddPathRow(root, 3, "クローズフォルダ", definition.ClosedFolder, target => BrowseFolder(target, "クローズフォルダを選択"));
        _promptPathBox = AddPromptRow(root, 4, definition.ProductPromptFilePath);

        AddLabel(root, 5, "有効／無効");
        _enabledBox = new WpfCheckBox
        {
            Content = "通常画面に表示する",
            IsChecked = definition.IsEnabled,
            Margin = new Thickness(8, 4, 0, 10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(_enabledBox, 5);
        Grid.SetColumn(_enabledBox, 1);
        Grid.SetColumnSpan(_enabledBox, 2);
        root.Children.Add(_enabledBox);

        _sortOrderBox = AddTextRow(root, 6, "表示順", definition.SortOrder.ToString());

        var buttonRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var okButton = new WpfButton { Content = "OK", Width = 88, IsDefault = true };
        var cancelButton = new WpfButton { Content = "キャンセル", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        okButton.Click += (_, _) => Accept();
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        Grid.SetRow(buttonRow, 7);
        Grid.SetColumn(buttonRow, 0);
        Grid.SetColumnSpan(buttonRow, 3);
        root.Children.Add(buttonRow);

        Content = root;
    }

    private static void AddLabel(Grid root, int row, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 4, 8, 10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        root.Children.Add(label);
    }

    private static WpfTextBox AddTextRow(Grid root, int row, string label, string value)
    {
        AddLabel(root, row, label);
        var box = new WpfTextBox { Text = value, Margin = new Thickness(8, 0, 0, 10), MinHeight = 28 };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 2);
        root.Children.Add(box);
        return box;
    }

    private static WpfTextBox AddPathRow(Grid root, int row, string label, string value, Action<WpfTextBox> browse)
    {
        AddLabel(root, row, label);
        var box = new WpfTextBox { Text = value, Margin = new Thickness(8, 0, 8, 10), MinHeight = 28 };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        root.Children.Add(box);

        var button = new WpfButton { Content = "参照...", Width = 92, Margin = new Thickness(0, 0, 0, 10) };
        button.Click += (_, _) => browse(box);
        Grid.SetRow(button, row);
        Grid.SetColumn(button, 2);
        root.Children.Add(button);
        return box;
    }

    private WpfTextBox AddPromptRow(Grid root, int row, string value)
    {
        AddLabel(root, row, "Codex指示ファイル");
        var box = new WpfTextBox { Text = value, Margin = new Thickness(8, 0, 8, 10), MinHeight = 28 };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        root.Children.Add(box);

        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var browse = new WpfButton { Content = "参照...", Width = 78 };
        var create = new WpfButton { Content = "空テンプレート作成", Width = 130, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => BrowsePromptFile(box);
        create.Click += (_, _) => CreateEmptyPromptFile(box);
        buttons.Children.Add(browse);
        buttons.Children.Add(create);
        Grid.SetRow(buttons, row);
        Grid.SetColumn(buttons, 2);
        root.Children.Add(buttons);
        return box;
    }

    private void BrowseFolder(WpfTextBox target, string description)
    {
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(target.Text) ? target.Text : string.Empty,
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                target.Text = dialog.SelectedPath;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"フォルダ参照を開始できませんでした。\n{ex.Message}", "参照エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowsePromptFile(WpfTextBox target)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "製品別Codex指示ファイルを選択",
                Filter = "テキストファイル (*.txt;*.md)|*.txt;*.md|すべてのファイル (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) == true)
            {
                target.Text = dialog.FileName;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"指示ファイルを参照できませんでした。\n{ex.Message}", "参照エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CreateEmptyPromptFile(WpfTextBox target)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "空の製品別Codex指示ファイルを作成",
                Filter = "テキストファイル (*.txt)|*.txt|Markdown (*.md)|*.md",
                FileName = BuildPromptFileName(ProductName),
                InitialDirectory = Path.Combine(AppContext.BaseDirectory, "prompts", "products"),
                AddExtension = true,
                DefaultExt = ".txt",
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var directory = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(dialog.FileName, string.Empty, EncodingPolicy.Utf8NoBom);
            target.Text = dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"空テンプレートを作成できませんでした。\n{ex.Message}", "作成エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Accept()
    {
        var candidate = new ProductDefinition
        {
            Id = ProductId,
            DisplayName = ProductName,
            Aliases = Aliases.ToList(),
            BaseFolder = BasePath,
            ClosedFolder = ClosedPath,
            ProductPromptFilePath = ProductPromptFilePath,
            IsEnabled = IsProductEnabled,
            SortOrder = SortOrder,
        };
        var errors = ProductDefinitionValidator.ValidateAll([candidate]);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "入力エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(ProductPromptFilePath))
        {
            var result = MessageBox.Show(
                this,
                "Codex指示ファイルが未設定です。共通指示だけで保存しますか？",
                "指示ファイル未設定",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        DialogResult = true;
    }

    private static IReadOnlyList<string> SplitAliases(string value)
    {
        return value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildPromptFileName(string productName)
    {
        var safe = string.Concat((productName ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch))
            .Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "product.txt" : $"{safe}.txt";
    }
}
