using SupportCaseManager.App.AiHandoff;

namespace SupportCaseManager.App.Tests.AiHandoff;

public class AiAssistantLaunchContextBuilderTests
{
    [Fact]
    public void BuildFromCurrentState_MapsCurrentStateToLaunchContext()
    {
        var builder = new AiAssistantLaunchContextBuilder();
        var state = new AiAssistantCurrentState
        {
            ProductName = " Klocwork ",
            BaseFolder = @"D:\Support\Open ",
            CloseFolder = @"D:\Support\Closed ",
            CaseFolderPath = @"D:\Support\Open\00017581",
            CompanyName = " 株式会社サンプル ",
            CustomerName = "山田 太郎",
            SupportNumber = " 00017581 ",
            Status = "対応中",
            ReceptionDate = new DateOnly(2026, 6, 3),
            NoteKind = "お客様ご相談内容",
            NoteFilePath = @"D:\Support\Open\00017581\お客様ご相談内容_00017581.txt",
            SelectedText = " 選択された問い合わせ ",
            CurrentNoteText = "ノート全文",
            AdditionalInstruction = "丁寧に",
        };

        var context = builder.BuildFromCurrentState(state);

        Assert.Equal("SupportCaseManager.App", context.Source);
        Assert.Equal("Klocwork", context.ProductName);
        Assert.Equal(@"D:\Support\Open", context.BaseFolder);
        Assert.Equal(@"D:\Support\Closed", context.CloseFolder);
        Assert.Equal(@"D:\Support\Open\00017581", context.CaseFolderPath);
        Assert.Equal("株式会社サンプル", context.CompanyName);
        Assert.Equal("山田 太郎", context.CustomerName);
        Assert.Equal("00017581", context.SupportNumber);
        Assert.Equal("対応中", context.Status);
        Assert.Equal(new DateOnly(2026, 6, 3), context.ReceptionDate);
        Assert.Equal("お客様ご相談内容", context.NoteKind);
        Assert.Equal(@"D:\Support\Open\00017581\お客様ご相談内容_00017581.txt", context.NoteFilePath);
        Assert.Equal("選択された問い合わせ", context.SelectedText);
        Assert.Equal("ノート全文", context.CurrentNoteText);
        Assert.Equal("選択された問い合わせ", context.InquiryText);
        Assert.Equal("丁寧に", context.AdditionalInstruction);
    }

    [Fact]
    public void BuildFromCurrentState_UsesCurrentNoteTextAsInquiryWhenSelectedTextIsEmpty()
    {
        var builder = new AiAssistantLaunchContextBuilder();

        var context = builder.BuildFromCurrentState(new AiAssistantCurrentState
        {
            SelectedText = " ",
            CurrentNoteText = "問い合わせ本文",
        });

        Assert.Equal("問い合わせ本文", context.InquiryText);
    }
}
