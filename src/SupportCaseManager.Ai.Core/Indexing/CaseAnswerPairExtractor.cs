using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Indexing;

public static partial class CaseAnswerPairExtractor
{
    private static readonly string[] QuestionHeadings = ["お客様ご相談内容", "お問い合わせ内容"];
    private static readonly string[] AnswerHeadings = ["お客様への返信案", "回答案", "メーカー回答"];
    private static readonly string[] InternalHeadings = ["社内メモ"];

    public static IReadOnlyList<CaseAnswerPair> Extract(
        CaseContext context,
        string caseFolderPath,
        string? productName = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pairs = new List<CaseAnswerPair>();
        foreach (var note in context.Notes)
        {
            var sections = ParseSections(note.Text);
            if (!string.IsNullOrWhiteSpace(sections.Question) && !string.IsNullOrWhiteSpace(sections.Answer))
            {
                pairs.Add(CreatePair(
                    context,
                    caseFolderPath,
                    productName,
                    sections.Question,
                    sections.Answer,
                    sections.InternalMemo,
                    note.NoteKind,
                    note.FilePath,
                    note.LastModifiedAt));
            }
        }

        var questionNotes = context.Notes.Where(note => IsHeading(note.NoteKind, QuestionHeadings)).ToList();
        var answerNotes = context.Notes.Where(note => IsHeading(note.NoteKind, AnswerHeadings)).ToList();
        var internalMemo = context.Notes
            .Where(note => IsHeading(note.NoteKind, InternalHeadings))
            .OrderByDescending(static note => note.LastModifiedAt)
            .FirstOrDefault()?.Text ?? string.Empty;
        foreach (var question in questionNotes)
        {
            var answer = answerNotes
                .OrderBy(note => TimestampDistance(question.LastModifiedAt, note.LastModifiedAt))
                .ThenByDescending(static note => note.LastModifiedAt)
                .FirstOrDefault();
            if (answer is null || string.IsNullOrWhiteSpace(question.Text) || string.IsNullOrWhiteSpace(answer.Text))
            {
                continue;
            }

            pairs.Add(CreatePair(
                context,
                caseFolderPath,
                productName,
                question.Text,
                answer.Text,
                internalMemo,
                answer.NoteKind,
                answer.FilePath,
                Latest(question.LastModifiedAt, answer.LastModifiedAt)));
        }

        return pairs
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.NormalizedQuestion))
            .GroupBy(
                static pair => $"{pair.QuestionHash}\n{pair.CustomerReplyText.Trim()}",
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
    }

    private static CaseAnswerPair CreatePair(
        CaseContext context,
        string caseFolderPath,
        string? productName,
        string question,
        string answer,
        string internalMemo,
        string noteType,
        string sourceFile,
        DateTimeOffset? updatedAt)
    {
        var normalized = PastQuestionNormalizer.Normalize(question, context.CompanyName);
        var hash = PastQuestionNormalizer.Hash(normalized);
        var effectiveProduct = string.IsNullOrWhiteSpace(productName) ? context.ProductName ?? string.Empty : productName;
        var idSource = $"{effectiveProduct}|{context.SupportNumber}|{sourceFile}|{hash}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSource)))[..24].ToLowerInvariant();
        return new CaseAnswerPair
        {
            Id = id,
            ProductName = effectiveProduct,
            SupportNumber = context.SupportNumber ?? string.Empty,
            QuestionText = question.Trim(),
            CustomerReplyText = answer.Trim(),
            InternalMemo = internalMemo.Trim(),
            NoteType = noteType,
            SourceFile = sourceFile,
            CaseFolderPath = caseFolderPath,
            UpdatedAt = updatedAt,
            NormalizedQuestion = normalized,
            QuestionHash = hash,
        };
    }

    private static ParsedSections ParseSections(string text)
    {
        var question = new StringBuilder();
        var answer = new StringBuilder();
        var internalMemo = new StringBuilder();
        StringBuilder? current = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var headingText = heading.Groups["heading"].Value;
                current = IsHeading(headingText, QuestionHeadings)
                    ? question
                    : IsHeading(headingText, AnswerHeadings)
                        ? answer
                        : internalMemo;
                var inline = heading.Groups["content"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    current.AppendLine(inline);
                }

                continue;
            }

            current?.AppendLine(line);
        }

        return new ParsedSections(question.ToString().Trim(), answer.ToString().Trim(), internalMemo.ToString().Trim());
    }

    private static bool IsHeading(string value, IReadOnlyList<string> headings)
    {
        var normalized = value.Trim().Trim('【', '】', '[', ']', '#', '*', ' ', ':', '：');
        return headings.Any(heading => string.Equals(normalized, heading, StringComparison.OrdinalIgnoreCase));
    }

    private static double TimestampDistance(DateTimeOffset? left, DateTimeOffset? right)
    {
        return left.HasValue && right.HasValue ? Math.Abs((right.Value - left.Value).TotalSeconds) : double.MaxValue;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
    {
        return left > right ? left : right;
    }

    [GeneratedRegex(@"^\s*(?:[#*]+\s*)?[【\[]?(?<heading>お客様ご相談内容|お問い合わせ内容|お客様への返信案|回答案|メーカー回答|社内メモ)[】\]]?\s*(?:[:：]\s*)?(?<content>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    private sealed record ParsedSections(string Question, string Answer, string InternalMemo);
}
