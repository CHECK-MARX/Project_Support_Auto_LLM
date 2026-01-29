using System;
using System.Windows;

namespace SupportCaseManager.App.Dialogs;

public sealed class TemplateViewerDialog : Window
{
    private readonly Action<string, string> _saveCallback;
    private readonly Action<string, string> _renameCallback;
    private readonly Action<string> _deleteCallback;
    private readonly System.Windows.Controls.TextBox _textBox;
    private readonly System.Windows.Controls.Button _editButton;
    private string _name;
    private bool _editing;

    public TemplateViewerDialog(
        string name,
        string text,
        Action<string, string> saveCallback,
        Action<string, string> renameCallback,
        Action<string> deleteCallback)
    {
        _name = name;
        _saveCallback = saveCallback;
        _renameCallback = renameCallback;
        _deleteCallback = deleteCallback;

        Title = $"テンプレート {name}";
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        _textBox = new System.Windows.Controls.TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Height = 260
        };
        root.Children.Add(_textBox);

        var buttonRow = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var copyButton = new System.Windows.Controls.Button { Content = "コピー", Width = 80 };
        _editButton = new System.Windows.Controls.Button { Content = "編集", Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        var renameButton = new System.Windows.Controls.Button { Content = "名前変更", Width = 90, Margin = new Thickness(8, 0, 0, 0) };
        var deleteButton = new System.Windows.Controls.Button { Content = "削除", Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        var closeButton = new System.Windows.Controls.Button { Content = "閉じる", Width = 80, Margin = new Thickness(16, 0, 0, 0) };

        copyButton.Click += (_, _) =>
        {
            System.Windows.Clipboard.SetText(_textBox.Text);
            Close();
        };
        _editButton.Click += (_, _) => ToggleEdit();
        renameButton.Click += (_, _) => RenameTemplate();
        deleteButton.Click += (_, _) => DeleteTemplate();
        closeButton.Click += (_, _) => Close();

        buttonRow.Children.Add(copyButton);
        buttonRow.Children.Add(_editButton);
        buttonRow.Children.Add(renameButton);
        buttonRow.Children.Add(deleteButton);
        buttonRow.Children.Add(closeButton);
        root.Children.Add(buttonRow);

        Content = root;
    }

    private void ToggleEdit()
    {
        if (!_editing)
        {
            _textBox.IsReadOnly = false;
            _editButton.Content = "保存";
            _editing = true;
            return;
        }

        _saveCallback(_name, _textBox.Text);
        _textBox.IsReadOnly = true;
        _editButton.Content = "編集";
        _editing = false;
    }

    private void RenameTemplate()
    {
        var dialog = new InputDialog("テンプレート名変更", "新しいテンプレート名を入力してください:", _name)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newName = dialog.Value.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            return;
        }

        _renameCallback(_name, newName);
        _name = newName;
        Title = $"テンプレート {_name}";
    }

    private void DeleteTemplate()
    {
        var result = System.Windows.MessageBox.Show(this, $"テンプレート『{_name}』を削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _deleteCallback(_name);
        Close();
    }
}
