using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Facts;

public interface IQuestionClassifier
{
    QuestionClassificationResult Classify(string inquiryText, InquiryFocus? inquiryFocus = null);
}
