using System.Windows;
using System.Windows.Controls;
using SupportCaseManager.Core.Cases;
using Button = System.Windows.Controls.Button;
using GroupBox = System.Windows.Controls.GroupBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace SupportCaseManager.App.Dialogs;

public sealed class GptHandoffImportPreviewDialog : Window
{
    public GptHandoffImportPreviewDialog(GptHandoffSnapshot snapshot)
    {
        Title = "GPT引継ぎ情報プレビュー";
        Width = 900;
        Height = 760;
        MinWidth = 720;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "以下の内容を案件のGPT連携履歴へ取り込みます。内容を確認してください。",
            Margin = new Thickness(0, 0, 0, 12),
        });

        var sections = new StackPanel();
        foreach (var section in GptHandoffFormat.SectionOrder)
        {
            var text = new TextBox
            {
                Text = snapshot[section],
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 56,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            sections.Children.Add(new GroupBox
            {
                Header = GptHandoffFormat.Label(section),
                Content = text,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        var scroll = new ScrollViewer
        {
            Content = sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var import = new Button { Content = "取り込む", Width = 110, IsDefault = true };
        import.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(import);
        buttons.Children.Add(new Button
        {
            Content = "キャンセル",
            Width = 110,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        });
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }
}
