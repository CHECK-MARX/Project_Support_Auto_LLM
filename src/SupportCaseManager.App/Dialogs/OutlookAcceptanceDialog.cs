using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SupportCaseManager.App.Outlook;
using SupportCaseManager.Core.Config;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace SupportCaseManager.App.Dialogs;

public sealed class OutlookAcceptanceDialog : Window
{
    private readonly ComboBox product = new();
    private readonly TextBox company = new();
    private readonly TextBox customer = new();
    private readonly TextBox email = new();
    private readonly TextBox subject = new();
    private readonly TextBox originalSentAt = new();
    private readonly TextBox inquiry = new();

    public bool Rejected { get; private set; }
    public string ProductName => product.SelectedItem as string ?? string.Empty;
    public string Company => company.Text.Trim();
    public string CustomerName => customer.Text.Trim();
    public string CustomerEmail => email.Text.Trim();
    public string Subject => subject.Text.Trim();
    public string OriginalSentAt => originalSentAt.Text.Trim();
    public string InquiryBody => inquiry.Text.Trim();

    public OutlookAcceptanceDialog(OutlookAcceptanceCandidate candidate, IReadOnlyList<ProductProfile> products)
    {
        Title = "新規受付メールを検出しました";
        Width = 850;
        Height = 760;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");

        var form = new Grid { Margin = new Thickness(18) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 9; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Add(0, "サポートID", new TextBlock { Text = candidate.SupportId });
        Add(1, "受付日時", new TextBlock { Text = candidate.ReceivedAt.ToString("yyyy/MM/dd HH:mm") });
        Add(2, "プロダクト", product);
        Add(3, "会社名", company);
        Add(4, "お客様名", customer);
        Add(5, "メールアドレス", email);
        Add(6, "元件名", subject);
        Add(7, "元メール日時", originalSentAt);
        var warning = new TextBlock
        {
            Text = candidate.HasOriginalMessage ? string.Empty : "お客様問い合わせ本文を取得できませんでした。内容を確認・補正してください。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.OrangeRed,
            Margin = new Thickness(0, 8, 0, 8)
        };
        Grid.SetRow(warning, 8);
        Grid.SetColumn(warning, 1);
        form.Children.Add(warning);
        Add(9, "お問い合わせ内容", inquiry);
        inquiry.AcceptsReturn = true;
        inquiry.TextWrapping = TextWrapping.Wrap;
        inquiry.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        inquiry.MinHeight = 170;

        foreach (var entry in products.Where(static item => item.IsEnabled).OrderBy(static item => item.SortOrder))
            product.Items.Add(entry.Name);
        var suggested = products.Where(static item => item.IsEnabled)
            .Where(entry => candidate.OriginalSubject.Contains(entry.Name, StringComparison.OrdinalIgnoreCase)
                || entry.Aliases.Any(alias => alias.Length > 2
                    && candidate.OriginalSubject.Contains(alias, StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToArray();
        if (suggested.Length == 1) product.SelectedItem = suggested[0].Name;
        company.Text = candidate.Company;
        customer.Text = candidate.CustomerName;
        email.Text = candidate.CustomerEmail;
        subject.Text = candidate.OriginalSubject;
        originalSentAt.Text = candidate.OriginalSentAt;
        inquiry.Text = candidate.InquiryBody;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var register = new Button { Content = "WPFへ登録", MinWidth = 125, Margin = new Thickness(0, 0, 8, 0) };
        register.Click += (_, _) =>
        {
            if (ProductName.Length == 0 || Company.Length == 0 || CustomerName.Length == 0
                || CustomerEmail.Length == 0 || Subject.Length == 0 || OriginalSentAt.Length == 0
                || InquiryBody.Length == 0 || !System.Net.Mail.MailAddress.TryCreate(CustomerEmail, out _))
            {
                MessageBox.Show(this, "製品・会社名・お客様名・メールアドレス・元件名・元日時・お問い合わせ内容を確認してください。",
                    "登録内容の確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(register);
        var reject = new Button { Content = "登録しない", MinWidth = 110 };
        reject.Click += (_, _) => { Rejected = true; DialogResult = false; };
        buttons.Children.Add(reject);

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;

        void Add(int row, string label, UIElement value)
        {
            var caption = new TextBlock { Text = label, Margin = new Thickness(0, 5, 10, 5) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "AppForeground");
            Grid.SetRow(caption, row);
            form.Children.Add(caption);
            if (value is TextBlock text)
                text.SetResourceReference(TextBlock.ForegroundProperty, "AppForeground");
            value.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            form.Children.Add(value);
        }
    }
}
