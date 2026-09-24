using System.Windows;
using System.Windows.Controls;
using SupportCaseManager.Core.Quality;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace SupportCaseManager.App.Dialogs;

public sealed class QualityMemoryApprovalDialog : Window
{
    private readonly ComboBox intentBox;
    private readonly ComboBox originBox;
    private readonly CheckBox outboundConfirmation;

    public QualityMemoryApprovalDialog(string product, string audience, string source, string text)
    {
        Title = "品質改善に登録";
        Width = 760;
        Height = 600;
        MinWidth = 580;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "この保存済み文章を文体・構成の参考として登録します。技術的な事実の根拠には使用しません。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(new TextBlock { Text = $"製品: {product}　対象: {audience}　元ファイル: {source}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "用途", Margin = new Thickness(0, 12, 0, 4) });
        intentBox = new ComboBox { MinWidth = 260 };
        foreach (var intent in audience == QualityAudience.Customer
                     ? new[] { "CUSTOMER_REPLY", "CUSTOMER_STATUS_UPDATE" }
                     : new[] { "MANUFACTURER_ASK", "MANUFACTURER_REPLY" })
            intentBox.Items.Add(intent);
        intentBox.SelectedIndex = 0;
        panel.Children.Add(intentBox);

        panel.Children.Add(new TextBlock { Text = "作成元（不明な場合はUNKNOWN）", Margin = new Thickness(0, 12, 0, 4) });
        originBox = new ComboBox { MinWidth = 260 };
        foreach (var origin in new[] { "UNKNOWN", "AI_DRAFT_EDITED", "GPT_REVISED", "MANUAL" }) originBox.Items.Add(origin);
        originBox.SelectedIndex = 0;
        panel.Children.Add(originBox);

        outboundConfirmation = new CheckBox
        {
            Content = audience == QualityAudience.Manufacturer
                ? "これはメーカーへ送るために採用した文章です（受信した回答ではありません）"
                : "これはお客様へ送るために採用した文章です",
            Margin = new Thickness(0, 14, 0, 12),
        };
        panel.Children.Add(outboundConfirmation);

        var preview = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var approve = new Button { Content = "承認して登録", MinWidth = 130, Margin = new Thickness(0, 0, 8, 0) };
        approve.Click += (_, _) =>
        {
            if (outboundConfirmation.IsChecked != true)
            {
                MessageBox.Show(this, "送付用に採用した文章であることを確認してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(approve);
        buttons.Children.Add(new Button { Content = "キャンセル", MinWidth = 100, IsCancel = true });

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(panel);
        Grid.SetRow(preview, 1);
        root.Children.Add(preview);
        Grid.SetRow(buttons, 2);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(buttons);
        Content = root;
    }

    public string Intent => intentBox.SelectedItem as string ?? string.Empty;
    public string Origin => originBox.SelectedItem as string ?? "UNKNOWN";
}
