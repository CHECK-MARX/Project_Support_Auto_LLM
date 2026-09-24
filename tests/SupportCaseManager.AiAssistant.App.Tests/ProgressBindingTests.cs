using System.Xml.Linq;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class ProgressBindingTests
{
    [Fact]
    public void MainWindow_GptPolishCommandsAreAudienceSpecific()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var buttons = document.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "お客様向け回答案をGPTで推敲"
            && button.Attribute("Command")?.Value == "{Binding GptPolishCustomerCommand}");
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "メーカー英語案をGPTで推敲"
            && button.Attribute("Command")?.Value == "{Binding GptPolishManufacturerCommand}");
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "この回答をGPTで推敲"
            && button.Attribute("Command")?.Value == "{Binding GptPolishTechnicalAnswerCommand}");
        Assert.Contains(document.Descendants().Where(element => element.Name.LocalName == "GroupBox"
                && element.Attribute("Header")?.Value == "Codex技術回答案（編集可能）"),
            group => group.Descendants().Any(element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value == "{Binding GptPolishTechnicalAnswerCommand}"));
        Assert.Contains(document.Descendants().Where(element => element.Name.LocalName == "GroupBox"
                && element.Attribute("Header")?.Value == "メーカー向け確認メール案（英語・編集可能）"),
            group => group.Descendants().Any(element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value == "{Binding GptPolishManufacturerCommand}"));
        Assert.Contains(XDocument.Load(FindMainWindowPath()).Descendants(), element =>
            element.Name.LocalName == "CheckBox" && element.Attribute("Content")?.Value == "Quality Review"
            && element.Attribute("IsChecked")?.Value == "{Binding QualityReviewEnabled}");
    }

    [Fact]
    public void MainWindow_SendButtonAndCtrlEnterUseTheSameUiCommand()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var sendButton = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Content")?.Value == "送信");
        var ctrlEnter = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "KeyBinding" &&
            element.Attribute("Key")?.Value == "Enter" &&
            element.Attribute("Modifiers")?.Value == "Control");

        Assert.Equal("{Binding Codex.SendFromUiCommand}", sendButton.Attribute("Command")?.Value);
        Assert.Equal("OnCodexSendButtonClick", sendButton.Attribute("Click")?.Value);
        Assert.Equal(sendButton.Attribute("Command")?.Value, ctrlEnter.Attribute("Command")?.Value);
    }

    [Fact]
    public void MainWindow_CodexOperationsAreStateConditionalAndExplainTheirPurpose()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var buttons = document.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "新しい調査を開始"
            && button.Attribute("ToolTip")?.Value?.Contains("別に新しいCodex Thread", StringComparison.Ordinal) == true);
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "前回の続きから再開"
            && button.Attribute("Visibility")?.Value.Contains("ShowResumeButton", StringComparison.Ordinal) == true);
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "Codexへ接続"
            && button.Attribute("Visibility")?.Value.Contains("ShowConnectButton", StringComparison.Ordinal) == true);
        Assert.Contains(buttons, button => button.Attribute("Content")?.Value == "再接続"
            && button.Attribute("Visibility")?.Value.Contains("ShowReconnectButton", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "Expander"
            && element.Attribute("Header")?.Value == "補助操作");
        Assert.Contains(document.Descendants().SelectMany(element => element.Attributes()), attribute =>
            attribute.Value.Contains("Codex.SendAvailabilityMessage", StringComparison.Ordinal));
        Assert.Contains(document.Descendants().SelectMany(element => element.Attributes()), attribute =>
            attribute.Value.Contains("GptPolishStatusText", StringComparison.Ordinal));
    }

    [Fact]
    public void MainWindow_ProgressBarUsesOneWayBindingForReadOnlyProgress()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var progressBar = FindProgressBar(document, "OperationProgressPercent");
        var value = Assert.Single(
            progressBar.Attributes(),
            attribute => attribute.Name.LocalName == "Value").Value;

        Assert.Contains("OperationProgressPercent", value, StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", value, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CodexProgressAndEditableDraftsAreBound()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var codexProgress = FindProgressBar(document, "Codex.ProgressPercent");
        var progressValue = Assert.Single(
            codexProgress.Attributes(),
            attribute => attribute.Name.LocalName == "Value").Value;
        Assert.Contains("Codex.ProgressPercent", progressValue, StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", progressValue, StringComparison.Ordinal);

        AssertEditableTextBox(document, "CustomerReplyDraft");
        AssertEditableTextBox(document, "InternalMemo");

        foreach (var bindingName in new[] { "Codex.Version", "Codex.ActualModelAndReasoning", "Codex.DiagnosticsPath" })
        {
            var bindings = document.Descendants()
                .SelectMany(static element => element.Attributes())
                .Where(attribute => attribute.Value.Contains(bindingName, StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(bindings);
            Assert.All(bindings, binding => Assert.Contains("Mode=OneWay", binding.Value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MainWindow_ManufacturerDraftActionsRemainAndUnusedMemoActionsAreRemoved()
    {
        var xaml = File.ReadAllText(FindMainWindowPath());

        Assert.Contains("メーカー向け確認メール案（日本語・編集可能）", xaml, StringComparison.Ordinal);
        Assert.Contains("メーカー向け確認メール案（英語・編集可能）", xaml, StringComparison.Ordinal);
        Assert.Contains("Codex.CopyJapaneseManufacturerDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Codex.CopyEnglishManufacturerDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Codex.SendEnglishManufacturerDraftToWpfNoteCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("この回答を調査メモへ反映", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("メモ反映を元に戻す", xaml, StringComparison.Ordinal);
        Assert.Contains("先にCodex調査タブでこの案件の調査を開始してください", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CodexMessageReadOnlyHeadersUseOneWayBindings()
    {
        var document = XDocument.Load(FindMainWindowPath());

        foreach (var bindingName in new[] { "RoleDisplay", "CreatedAtText" })
        {
            var runAttributes = document.Descendants()
                .Where(element => element.Name.LocalName == "Run")
                .SelectMany(static element => element.Attributes())
                .ToList();
            var binding = Assert.Single(runAttributes, attribute =>
                attribute.Name.LocalName == "Text" &&
                attribute.Value.Contains(bindingName, StringComparison.Ordinal));

            Assert.Contains("Mode=OneWay", binding.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainWindow_ArtifactPlanRequiresExplicitCommandAndKeepsMailEditable()
    {
        var document = XDocument.Load(FindMainWindowPath());

        AssertEditableTextBox(document, "Codex.ArtifactDestinationFolder");
        AssertEditableTextBox(document, "Codex.ArtifactOutputPlanText");
        AssertEditableTextBox(document, "Codex.JapaneseManufacturerDraft");
        AssertEditableTextBox(document, "Codex.EnglishManufacturerDraft");
        var xaml = File.ReadAllText(FindMainWindowPath());
        Assert.Contains("翻訳元ファイルを選択", xaml, StringComparison.Ordinal);
        Assert.Contains("日付を入れて保存", xaml, StringComparison.Ordinal);
        Assert.Contains("英訳ファイルを作成", xaml, StringComparison.Ordinal);
        Assert.Contains("Codex.ArtifactPreviewItems", xaml, StringComparison.Ordinal);
        Assert.Contains("メーカー向けメール案（日英・編集可能・自動送信しません）", xaml, StringComparison.Ordinal);
        Assert.Contains("WPFノート編集に送る（英語）", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("英訳Excelを作成", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex.ManufacturerMailDraft", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex.CopyManufacturerMailCommand", xaml, StringComparison.Ordinal);
        foreach (var commandName in new[]
                 {
                     "Codex.PrepareArtifactPlanCommand",
                     "Codex.CreateExcelArtifactCommand",
                     "Codex.CancelArtifactCommand",
                     "Codex.GenerateManufacturerMailCommand",
                     "Codex.GenerateManufacturerReplyCommand",
                     "Codex.CopyJapaneseManufacturerDraftCommand",
                     "Codex.CopyEnglishManufacturerDraftCommand",
                     "Codex.SendEnglishManufacturerDraftToWpfNoteCommand",
                 })
        {
            Assert.Contains(
                document.Descendants().SelectMany(static element => element.Attributes()),
                attribute => attribute.Value.Contains(commandName, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MainWindow_RagLabEvidenceSettingsAreOptionalAndBound()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var attributes = document.Descendants().SelectMany(static element => element.Attributes()).ToArray();

        foreach (var bindingName in new[]
                 {
                     "UseRagLabEvidence",
                     "RagLabEvidenceFilePath",
                     "RagLabBaselineReadinessFilePath",
                     "RagLabEvidenceMaxItems",
                     "SelectRagLabEvidenceFileCommand",
                     "SelectRagLabBaselineReadinessFileCommand",
                 })
        {
            Assert.Contains(attributes, attribute => attribute.Value.Contains(bindingName, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MainWindow_ReadOnlyCodexStatusRunsUseOneWayBinding()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var runTextAttributes = document.Descendants()
            .Where(element => element.Name.LocalName == "Run")
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "Text")
            .ToArray();

        foreach (var property in new[]
        {
            "Codex.ConnectionStateText",
            "Codex.CurrentThreadStatusText",
            "GptHandoffStatusText",
        })
        {
            var run = Assert.Single(runTextAttributes,
                attribute => attribute.Value.Contains(property, StringComparison.Ordinal));
            Assert.Contains("Mode=OneWay", run.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainWindow_GptHandoffButtonsBindToViewModelCommands()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var buttons = document.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToList();

        Assert.Contains(buttons, button =>
            string.Equals(button.Attribute("Content")?.Value, "GPT引継ぎ情報を作成", StringComparison.Ordinal) &&
            button.Attribute("Command")?.Value.Contains("CreateGptHandoffCommand", StringComparison.Ordinal) == true);
        Assert.Contains(buttons, button =>
            string.Equals(button.Attribute("Content")?.Value, "GPT引継ぎ情報を取り込む", StringComparison.Ordinal) &&
            button.Attribute("Command")?.Value.Contains("ImportGptHandoffCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void MainWindow_RagLabAbComparisonUsesExplicitUserCommandsAndReadOnlyResult()
    {
        var document = XDocument.Load(FindMainWindowPath());
        var attributes = document.Descendants().SelectMany(static element => element.Attributes()).ToArray();

        foreach (var commandName in new[]
                 {
                     "Codex.CaptureAbBaselineCommand",
                     "Codex.CaptureAbEvidenceCommand",
                     "Codex.CompareAbCommand",
                 })
        {
            Assert.Contains(attributes, attribute => attribute.Value.Contains(commandName, StringComparison.Ordinal));
        }

        var comparisonText = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attributes().Any(attribute => attribute.Value.Contains("Codex.AbComparisonText", StringComparison.Ordinal)));
        Assert.Equal("True", comparisonText.Attribute("IsReadOnly")?.Value);
        Assert.Contains("Mode=OneWay", comparisonText.Attribute("Text")?.Value, StringComparison.Ordinal);
    }

    private static XElement FindProgressBar(XDocument document, string bindingText)
    {
        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ProgressBar" &&
                       element.Attributes().Any(attribute =>
                           attribute.Name.LocalName == "Value" &&
                           attribute.Value.Contains(bindingText, StringComparison.Ordinal)));
    }

    private static void AssertEditableTextBox(XDocument document, string bindingText)
    {
        var textBox = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "TextBox" &&
                       element.Attributes().Any(attribute =>
                           attribute.Name.LocalName == "Text" &&
                           attribute.Value.Contains(bindingText, StringComparison.Ordinal)));
        Assert.Equal("False", textBox.Attribute("IsReadOnly")?.Value);
        Assert.Contains("Mode=TwoWay", textBox.Attribute("Text")!.Value, StringComparison.Ordinal);
    }

    private static string FindMainWindowPath()
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
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("MainWindow.xaml was not found.");
    }
}
