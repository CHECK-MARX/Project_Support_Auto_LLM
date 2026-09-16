using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace SupportCaseManager.App.Dialogs;

public enum GptRegistrationPreviewAction
{
    Cancel,
    CreateNew,
    LinkExisting,
}

public sealed class GptRegistrationPreviewDialog : Window
{
    private readonly TextBox briefBox;

    public GptRegistrationPreviewAction SelectedAction { get; private set; }
    public string ApprovedBrief => briefBox.Text.Trim();

    public GptRegistrationPreviewDialog(
        string targetDisplayName,
        string supportId,
        string caseKey,
        string brief)
    {
        Title = "GPT登録内容プレビュー";
        Width = 900;
        Height = 720;
        MinWidth = 720;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summary = new TextBlock
        {
            Text = $"対象: {targetDisplayName}\nSupport ID: {supportId}\n案件キー: {caseKey}",
            Margin = new Thickness(0, 0, 0, 12),
        };
        root.Children.Add(summary);

        briefBox = new TextBox
        {
            Text = brief,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(briefBox, 1);
        root.Children.Add(briefBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(CreateButton("新しいGPTチャットを登録", GptRegistrationPreviewAction.CreateNew, isDefault: true));
        buttons.Children.Add(CreateButton("既存GPTチャットを紐付け", GptRegistrationPreviewAction.LinkExisting));
        var cancel = new Button
        {
            Content = "キャンセル",
            Width = 100,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    private Button CreateButton(string text, GptRegistrationPreviewAction action, bool isDefault = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 170,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
        };
        button.Click += (_, _) =>
        {
            if (action == GptRegistrationPreviewAction.CreateNew && string.IsNullOrWhiteSpace(ApprovedBrief))
            {
                MessageBox.Show(this, "GPT登録内容を入力してください。", "入力確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectedAction = action;
            DialogResult = true;
        };
        return button;
    }
}

public sealed class GptConversationLinkDialog : Window
{
    private readonly TextBox urlBox;

    public string ConversationUrl => urlBox.Text.Trim();

    public GptConversationLinkDialog(string? initialUrl = null)
    {
        Title = "既存GPTチャットを紐付け";
        Width = 700;
        Height = 180;
        MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock { Text = "ChatGPT Conversation URL" });
        urlBox = new TextBox
        {
            Text = initialUrl ?? string.Empty,
            Margin = new Thickness(0, 6, 0, 12),
            MinHeight = 28,
        };
        Grid.SetRow(urlBox, 1);
        root.Children.Add(urlBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var link = new Button { Content = "紐付け", Width = 100, IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 100, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        link.Click += (_, _) =>
        {
            if (!SupportCaseManager.App.ChatGpt.GptConversationUrl.TryValidateConversation(ConversationUrl, out _))
            {
                MessageBox.Show(this, "Conversation URLは https://chatgpt.com/c/... 形式で指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        };
        buttons.Children.Add(link);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }
}
