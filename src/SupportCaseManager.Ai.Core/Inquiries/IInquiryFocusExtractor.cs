using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Inquiries;

public interface IInquiryFocusExtractor
{
    InquiryFocus Extract(string inquiryText, CaseContext? caseContext = null);
}
