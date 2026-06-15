using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Facts;

public interface IFactResolver
{
    FactResolutionResult Resolve(
        string productName,
        string aiIndexFolder,
        string inquiryText,
        InquiryFocus? inquiryFocus = null);
}
