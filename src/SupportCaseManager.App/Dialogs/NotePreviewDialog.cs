using System.Windows;

namespace SupportCaseManager.App.Dialogs;

public sealed class NotePreviewDialog : Window
{
    public NotePreviewDialog(string body)
    {
        Title = "本文プレビュー";
        Width = 560;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var textBox = new System.Windows.Controls.TextBox
        {
            Text = body,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Height = 300
        };
        root.Children.Add(textBox);

        var buttonRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var copyButton = new System.Windows.Controls.Button { Content = "コピー", Width = 80 };
        var closeButton = new System.Windows.Controls.Button { Content = "閉じる", Width = 80, Margin = new Thickness(8, 0, 0, 0) };

        copyButton.Click += (_, _) =>
        {
            var selected = textBox.SelectedText;
            System.Windows.Clipboard.SetText(string.IsNullOrEmpty(selected) ? textBox.Text : selected);
        };
        closeButton.Click += (_, _) => Close();

        buttonRow.Children.Add(copyButton);
        buttonRow.Children.Add(closeButton);
        root.Children.Add(buttonRow);

        Content = root;
    }
}
