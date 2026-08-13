using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class CodexCaseFileViewModel : ObservableObject
{
    private bool isSelected;
    private string confirmationStatus = "未確認";

    public CodexCaseFileViewModel(CodexCaseFileInfo file, bool selected)
    {
        File = file;
        isSelected = selected;
    }

    public CodexCaseFileInfo File { get; }
    public string FullPath => File.FullPath;
    public string FileName => File.FileName;
    public string RelativePath => File.RelativePath;
    public string KindText => File.Kind.ToString();
    public string SizeText => File.Size < 1024 ? $"{File.Size} B" : $"{File.Size / 1024d:N1} KB";
    public string LastModifiedText => File.LastModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public bool CanSendToCodex => File.CanSendToCodex;
    public bool IsImageInput => File.IsImageInput;
    public string ExclusionReason => File.ExclusionReason;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value && CanSendToCodex);
    }

    public string ConfirmationStatus
    {
        get => confirmationStatus;
        set => SetProperty(ref confirmationStatus, value);
    }
}
