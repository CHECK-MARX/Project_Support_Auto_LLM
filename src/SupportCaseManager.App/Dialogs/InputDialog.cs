using System.Windows;

namespace SupportCaseManager.App.Dialogs;

public sealed class InputDialog : Window
{
    private readonly System.Windows.Controls.TextBox _textBox;

    public InputDialog(string title, string message, string? defaultValue = null)
    {
        Title = title;
        Width = 420;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8) });

        _textBox = new System.Windows.Controls.TextBox { Text = defaultValue ?? string.Empty };
        root.Children.Add(_textBox);

        var buttonRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancelButton = new System.Windows.Controls.Button { Content = "キャンセル", Width = 80, IsCancel = true };

        okButton.Click += (_, _) =>
        {
            Value = _textBox.Text;
            DialogResult = true;
        };

        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        root.Children.Add(buttonRow);

        Content = root;
    }

    public string Value { get; private set; } = string.Empty;
}
