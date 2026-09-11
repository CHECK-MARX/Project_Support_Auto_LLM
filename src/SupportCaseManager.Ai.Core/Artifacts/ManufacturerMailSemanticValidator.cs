using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ManufacturerMailSemanticResult
{
    public IReadOnlyList<string> Issues { get; init; } = [];
    public int QuestionCount { get; init; }
    public int CoveredQuestions { get; init; }
    public int PriorCount { get; init; }
    public int CoveredPrior { get; init; }
    public int IntentCount { get; init; }
    public int CoveredIntent { get; init; }
    public int ForbiddenTopicHits { get; init; }
    public bool Succeeded => Issues.Count == 0;
    public string Diagnostic => $"Mail Brief Questions: {QuestionCount}\nMail Brief Prior Response Points: {PriorCount}\nMail Brief Customer Intent: {IntentCount}\nMail Question Coverage: {CoveredQuestions}/{QuestionCount}\nMail Prior Response Coverage: {CoveredPrior}/{PriorCount}\nMail Intent Coverage: {CoveredIntent}/{IntentCount}\nMail Semantic Coverage: {(Succeeded ? "100%" : "INCOMPLETE")}\nForbidden Topic Hits: {ForbiddenTopicHits}";
}

public sealed class ManufacturerMailSemanticValidator
{
    public ManufacturerMailSemanticResult Validate(ManufacturerDraftPair pair, ManufacturerMailBrief brief)
    {
        var issues = new List<string>();
        if (!brief.CloseRequested && (ManufacturerMailConcepts.HasCloseRequest(pair.JapaneseDraft)
            || ManufacturerMailConcepts.HasCloseRequest(pair.EnglishDraft)))
            issues.Add("UnsupportedCustomerIntent.CloseCase");
        if (!brief.Ready) issues.Add("Brief.SourceQuestionsOrPriorResponseMissing");
        var japanese = Questions(pair.JapaneseDraft);
        var english = Questions(pair.EnglishDraft);
        var coveredQuestions = 0;
        if (brief.IsAttachmentCentricFollowUp)
        {
            if (!ContainsAdditionalQuestionMention(pair.JapaneseDraft)
                || !ContainsAdditionalQuestionMention(pair.EnglishDraft))
            {
                issues.Add("CurrentCustomerAdditionalQuestionMention");
            }
            var japaneseBody = BodyWithoutSignature(pair.JapaneseDraft);
            var englishBody = BodyWithoutSignature(pair.EnglishDraft);
            foreach (var topic in brief.MajorTechnicalTopics)
            {
                if (japaneseBody.Contains(topic, StringComparison.Ordinal)
                    && englishBody.Contains(topic, StringComparison.Ordinal)) coveredQuestions++;
                else issues.Add($"MajorTechnicalTopicCoverage.{topic}");
            }
            if (!string.IsNullOrWhiteSpace(brief.Recipient.DisplayName)
                && brief.Recipient.IsResolved
                && (!pair.JapaneseDraft.Contains(brief.Recipient.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || !pair.EnglishDraft.Contains(brief.Recipient.DisplayName, StringComparison.OrdinalIgnoreCase)))
                issues.Add("ActualManufacturerRecipient");
        }
        else
        {
            if (japanese.Count != brief.CurrentQuestions.Count || english.Count != brief.CurrentQuestions.Count)
                issues.Add("CurrentQuestionCoverage.Count");
            for (var i = 0; i < brief.CurrentQuestions.Count; i++)
            {
                var point = brief.CurrentQuestions[i];
                if (japanese.TryGetValue(i + 1, out var ja) && english.TryGetValue(i + 1, out var en)
                    && Covers(ja, point) && Covers(en, point)) coveredQuestions++;
                else issues.Add($"CurrentQuestionCoverage.{point.Id}");
            }
        }
        // Prior facts and intent must appear in the narrative, not merely in the questions.
        var jaNarrative = Narrative(pair.JapaneseDraft);
        var enNarrative = Narrative(pair.EnglishDraft);
        var priorCovered = brief.IsAttachmentCentricFollowUp ? 0
            : Coverage(brief.PriorResponseSummaryPoints, jaNarrative, enNarrative, "PriorResponseCoverage", issues);
        var intentCovered = brief.IsAttachmentCentricFollowUp ? 0
            : Coverage(brief.CurrentCustomerIntent, jaNarrative, enNarrative, "CustomerIntentCoverage", issues);
        var expected = brief.CurrentOutboundAttachments
            .Select(NormalizeAttachmentName)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, text) in new[] { ("Japanese", pair.JapaneseDraft), ("English", pair.EnglishDraft) })
        {
            var actual = ExtractAttachmentFileNames(text);
            if (!expected.SetEquals(actual)) issues.Add($"AttachmentExactMatch.{language}");
        }
        var hits = brief.ForbiddenClaims.Count(topic =>
        {
            var found = ManufacturerMailConcepts.HasTopic(pair.JapaneseDraft, topic)
                || ManufacturerMailConcepts.HasTopic(pair.EnglishDraft, topic);
            if (found) issues.Add($"ForbiddenTopic.{topic}");
            return found;
        });
        return new ManufacturerMailSemanticResult
        {
            Issues = issues, QuestionCount = brief.CurrentQuestions.Count, CoveredQuestions = coveredQuestions,
            PriorCount = brief.PriorResponseSummaryPoints.Count, CoveredPrior = priorCovered,
            IntentCount = brief.CurrentCustomerIntent.Count, CoveredIntent = intentCovered, ForbiddenTopicHits = hits,
        };
    }

    private static bool Covers(string text, ManufacturerMailBriefPoint point) => point.Concepts.Count > 0
        && point.Concepts.All(concept => ManufacturerMailConcepts.Covers(text, concept));

    private static int Coverage(IReadOnlyList<ManufacturerMailBriefPoint> points, string ja, string en,
        string category, List<string> issues)
    {
        var covered = 0;
        foreach (var point in points)
            if (Covers(ja, point) && Covers(en, point)) covered++;
            else issues.Add($"{category}.{point.Id}");
        return covered;
    }

    private static Dictionary<int, string> Questions(string text)
    {
        var result = new Dictionary<int, string>();
        var matches = Regex.Matches(text, @"(?:質問|Question)\s*(\d+)\s*[:：.．]", RegexOptions.IgnoreCase);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var paragraph = text[start..end];
            // A blank line terminates a question; trailing attachment/signature text cannot supply coverage.
            paragraph = Regex.Split(paragraph, @"\r?\n\s*\r?\n")[0];
            if (!result.TryAdd(int.Parse(matches[i].Groups[1].Value), paragraph)) return [];
        }
        return result;
    }

    private static string Narrative(string text)
    {
        var first = Regex.Match(text, @"(?:質問|Question)\s*\d+\s*[:：.．]", RegexOptions.IgnoreCase);
        return first.Success ? text[..first.Index] : text;
    }

    private static string BodyWithoutSignature(string text)
    {
        var match = Regex.Match(text, @"(?im)^\s*(?:Best regards|Kind regards|Sincerely|よろしくお願いいたします)\b");
        return match.Success ? text[..match.Index] : text;
    }

    private static bool ContainsAdditionalQuestionMention(string text) =>
        Regex.IsMatch(text, @"追加(?:の)?質問|追加確認|additional questions?|follow.?up questions?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static HashSet<string> ExtractAttachmentFileNames(string text) =>
        Regex.Matches(text,
                @"(?<![A-Za-z0-9_.-])[A-Za-z0-9][A-Za-z0-9_().-]*\.(?:xlsx|csv|pdf|zip|xml|txt|md|docx|pptx)(?![A-Za-z0-9_-])"
                + @"|(?<![\p{L}\p{N}_-])[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}][\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}\p{N}_().-]*\.(?:xlsx|csv|pdf|zip|xml|txt|md|docx|pptx)(?![A-Za-z0-9_-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => NormalizeAttachmentName(match.Value))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeAttachmentName(string value)
    {
        var trimmed = value.Trim().Trim('"', '\'', '(', ')', '[', ']', '{', '}', '、', ',', '。', ';', ':');
        return Path.GetFileName(trimmed);
    }
}
