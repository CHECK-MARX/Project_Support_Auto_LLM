using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public static class TopicEntityEvidenceRanker
{
    public static QuestionAwareEvidenceRankingResult Rank(
        IEnumerable<SearchSourceViewModel> candidates,
        QuestionAwareEvidenceSelectionContext context,
        int maxItems)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        var items = candidates.ToList();
        var catalog = SupportTopicCatalog.Create(context.ProductName);
        var topicAnalysis = context.UsePhase175QualityControls
            ? NegationAwareTopicAnalyzer.Analyze(context.InquiryText, catalog)
            : null;
        var queryProfile = topicAnalysis?.PrimaryProfile ?? TopicEntityAnalyzer.Extract(context.InquiryText, catalog);
        var technicalTokens = QuestionAwareEvidenceRanker.ExtractExactTechnicalTokens(context.InquiryText);
        var rankingCandidates = items.Select((item, index) => new TopicEntityRankingCandidate
        {
            CandidateIndex = index,
            CandidateId = string.IsNullOrWhiteSpace(item.SourceId) ? $"candidate-{index}" : item.SourceId,
            Text = DocumentText(item),
            SourceType = item.SourceType,
            ProductName = item.ProductName,
            BaseSearchScore = item.Score ?? 0,
            OriginalRank = index + 1,
            IsManuallySelected = item.IsManuallySelected,
            Profile = TopicEntityAnalyzer.Extract(DocumentText(item), catalog),
        }).ToList();
        var ranked = TopicEntityRanker.Rank(new TopicEntityRankingRequest
        {
            QueryProfile = queryProfile,
            ExcludedProfile = topicAnalysis?.ExcludedProfile ?? new TopicEntityProfile(),
            TechnicalTokens = technicalTokens,
            RequestedProduct = context.ProductName,
            RequestedVersion = context.TargetVersion,
            Candidates = rankingCandidates,
            MaxItems = maxItems,
        });
        var classification = new QuestionClassifier().Classify(context.InquiryText);

        return new QuestionAwareEvidenceRankingResult
        {
            Ranked = ranked.Selected.Select(assessment => new QuestionAwareEvidenceAssessment
            {
                Item = items[assessment.CandidateIndex],
                FinalScore = assessment.FinalScore,
                QuestionTypeScore = assessment.IntentScore,
                TechnicalTokenScore = assessment.TechnicalTokenScore,
                SourceTrustScore = assessment.SourceTrustScore,
                Coverage = assessment.Coverage,
                ExactTechnicalTokens = assessment.ExactTechnicalTokens,
                ProductMatch = assessment.ProductMatch,
                VersionMatch = assessment.VersionMatch,
                TextFingerprint = assessment.TextFingerprint,
                TopicScore = assessment.TopicScore,
                EntityScore = assessment.EntityScore,
                ConflictPenalty = assessment.ConflictPenalty,
                ExclusionPenalty = assessment.ExclusionPenalty,
                ExplicitlyExcluded = assessment.ExplicitlyExcluded,
                TopicConflict = assessment.TopicConflict,
                SelectionReason = assessment.SelectionReason,
            }).ToList(),
            FinalCoverage = ranked.FinalCoverage,
            QuestionTypes = classification.QuestionTypes,
            InsufficientReasons = ranked.InsufficientReasons,
            RankingMode = EvidenceRankingModes.Phase16,
        };
    }

    private static string DocumentText(SearchSourceViewModel item) => string.Join(
        '\n',
        item.Title,
        item.Source.QuestionText,
        item.Source.InternalMemo,
        item.Text);

}
