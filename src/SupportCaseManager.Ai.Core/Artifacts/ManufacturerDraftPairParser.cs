using System.Text.Json;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ManufacturerDraftPair
{
    public string JapaneseDraft { get; init; } = string.Empty;
    public string EnglishDraft { get; init; } = string.Empty;
}

public sealed record ManufacturerDraftParseResult
{
    public bool Succeeded { get; init; }
    public ManufacturerDraftPair? Pair { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record ManufacturerProtectedValue
{
    public string Literal { get; init; } = string.Empty;
    public string Category { get; init; } = "Technical";
    public string Source { get; init; } = "CurrentCase";
    public bool RequiredInBothDrafts { get; init; } = true;
}

public sealed record ManufacturerProtectedValueSet
{
    public IReadOnlyList<ManufacturerProtectedValue> Items { get; init; } = [];

    public IReadOnlyList<string> Values => Items
        .Where(static item => item.RequiredInBothDrafts && !string.IsNullOrWhiteSpace(item.Literal))
        .Select(static item => item.Literal)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<string> TechnicalValues => Items
        .Where(static item => string.Equals(item.Category, "Technical", StringComparison.Ordinal))
        .Select(static item => item.Literal)
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public int Count => Values.Count;

    public ManufacturerProtectedValueParity EvaluatePromptInjection(string prompt)
    {
        var expected = Values;
        var injected = expected
            .Where(value => prompt.Contains(value, StringComparison.Ordinal))
            .ToArray();
        return new ManufacturerProtectedValueParity
        {
            ExpectedCount = expected.Count,
            InjectedCount = injected.Length,
            MissingCount = 0,
        };
    }

    public ManufacturerProtectedValueParity EvaluateDraftParity(
        string prompt,
        ManufacturerDraftValidationResult validation)
    {
        var injection = EvaluatePromptInjection(prompt);
        var missing = validation.MissingJapaneseProtectedValues
            .Concat(validation.MissingEnglishProtectedValues)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return injection with { MissingCount = missing };
    }
}

public sealed record ManufacturerProtectedValueParity
{
    public int ExpectedCount { get; init; }
    public int InjectedCount { get; init; }
    public int MissingCount { get; init; }
    public bool PromptParity => ExpectedCount == InjectedCount;
    public bool IsComplete => PromptParity && MissingCount == 0;
}

public sealed record ManufacturerDraftValidationResult
{
    public bool Succeeded { get; init; }
    public bool QuestionParity { get; init; }
    public bool AttachmentParity { get; init; }
    public bool TechnicalParity { get; init; }
    public IReadOnlyList<int> JapaneseQuestionNumbers { get; init; } = [];
    public IReadOnlyList<int> EnglishQuestionNumbers { get; init; } = [];
    public IReadOnlyList<string> MissingJapaneseProtectedValues { get; init; } = [];
    public IReadOnlyList<string> MissingEnglishProtectedValues { get; init; } = [];
    public bool DataBoundary { get; init; }
    public IReadOnlyList<string> DataBoundaryViolations { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record ManufacturerDraftBoundaryContext
{
    public string CustomerPersonName { get; init; } = string.Empty;
    public string CustomerCompanyName { get; init; } = string.Empty;
    public IReadOnlyList<string> ProhibitedAttachmentNames { get; init; } = [];
}

public sealed class ManufacturerDraftPairParser
{
    private static readonly HashSet<string> NonCustomerFacingMetadata = new(StringComparer.Ordinal)
    {
        "EvidenceId",
        "SourceId",
        "ContentHash",
        "CurrentCase",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex JapaneseQuestionNumber = new(
        @"質問\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishQuestionNumber = new(
        @"(?:Question|Q)\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex[] TechnicalValuePatterns =
    [
        new(@"\b[A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9_]+)+(?:\(\))?", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<!\.)\b(?:[A-Z][a-z0-9]+){2,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\b[a-z]+[A-Z][A-Za-z0-9]*\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\b[A-Z]{2,}[A-Z0-9_-]*\d[A-Z0-9_-]*\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\b\d{5,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\b\d+\.\d+(?:\.\d+)+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\b(?:qacli|qac-cli)(?:\s+[a-z][a-z0-9-]*)?(?:\s+--?[A-Za-z0-9][A-Za-z0-9_-]*)*", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"(?<!\w)--?[A-Za-z][A-Za-z0-9_-]*", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    private static readonly Regex TechnicalFileStem = new(
        @"^(?:[A-Z]{2,}[A-Z0-9_-]*\d[A-Z0-9_-]*|\d{5,})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ManufacturerDraftParseResult Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return Failure("Codexから日英メール案が返されませんでした。");
        }

        var json = UnwrapJson(response);
        try
        {
            var pair = JsonSerializer.Deserialize<ManufacturerDraftPair>(json, JsonOptions);
            if (pair is null
                || string.IsNullOrWhiteSpace(pair.JapaneseDraft)
                || string.IsNullOrWhiteSpace(pair.EnglishDraft))
            {
                return Failure("日英両方のメーカー確認案が必要です。");
            }

            return new ManufacturerDraftParseResult
            {
                Succeeded = true,
                Pair = pair with
                {
                    JapaneseDraft = pair.JapaneseDraft.Trim(),
                    EnglishDraft = pair.EnglishDraft.Trim(),
                },
            };
        }
        catch (JsonException ex)
        {
            return Failure($"メーカー確認案の構造化応答を解釈できません: {ex.Message}");
        }
    }

    public ManufacturerDraftValidationResult Validate(
        ManufacturerDraftPair pair,
        IReadOnlyList<string> attachmentNames,
        IReadOnlyList<string> technicalValues)
    {
        var protectedValues = new ManufacturerProtectedValueSet
        {
            Items = technicalValues
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => new ManufacturerProtectedValue { Literal = value })
                .ToArray(),
        };
        return Validate(pair, protectedValues, attachmentNames);
    }

    public ManufacturerDraftValidationResult Validate(
        ManufacturerDraftPair pair,
        ManufacturerProtectedValueSet protectedValues,
        IReadOnlyList<string> attachmentNames,
        ManufacturerDraftBoundaryContext? boundaryContext = null)
    {
        var errors = new List<string>();
        var japaneseQuestions = ExtractQuestionNumbers(pair.JapaneseDraft, JapaneseQuestionNumber);
        var englishQuestions = ExtractQuestionNumbers(pair.EnglishDraft, EnglishQuestionNumber);
        var questionParity = japaneseQuestions.SequenceEqual(englishQuestions);
        if (!questionParity)
        {
            errors.Add($"日英の質問番号が一致しません。日本語={string.Join(", ", japaneseQuestions)} 英語={string.Join(", ", englishQuestions)}");
        }

        var allowedAttachments = attachmentNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeAttachmentName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var japaneseAttachments = ExtractAttachmentFileNames(pair.JapaneseDraft);
        var englishAttachments = ExtractAttachmentFileNames(pair.EnglishDraft);
        var missingJapaneseAttachments = allowedAttachments
            .Where(name => !japaneseAttachments.Contains(name))
            .ToArray();
        var missingEnglishAttachments = allowedAttachments
            .Where(name => !englishAttachments.Contains(name))
            .ToArray();
        var attachmentParity = missingJapaneseAttachments.Length == 0 && missingEnglishAttachments.Length == 0;
        foreach (var attachmentName in allowedAttachments)
        {
            if (missingJapaneseAttachments.Contains(attachmentName, StringComparer.Ordinal)
                || missingEnglishAttachments.Contains(attachmentName, StringComparer.Ordinal))
            {
                errors.Add($"添付ファイル名が日英案の両方にありません: {attachmentName}");
            }
        }

        var missingJapaneseProtectedValues = protectedValues.Values
            .Where(value => !pair.JapaneseDraft.Contains(value, StringComparison.Ordinal))
            .ToArray();
        var missingEnglishProtectedValues = protectedValues.Values
            .Where(value => !pair.EnglishDraft.Contains(value, StringComparison.Ordinal))
            .ToArray();
        var missingJapaneseTechnicalValues = protectedValues.TechnicalValues
            .Where(value => !pair.JapaneseDraft.Contains(value, StringComparison.Ordinal))
            .ToArray();
        var missingEnglishTechnicalValues = protectedValues.TechnicalValues
            .Where(value => !pair.EnglishDraft.Contains(value, StringComparison.Ordinal))
            .ToArray();
        var technicalParity = missingJapaneseTechnicalValues.Length == 0
            && missingEnglishTechnicalValues.Length == 0;
        foreach (var technicalValue in protectedValues.TechnicalValues)
        {
            if (missingJapaneseProtectedValues.Contains(technicalValue, StringComparer.Ordinal)
                || missingEnglishProtectedValues.Contains(technicalValue, StringComparer.Ordinal))
            {
                errors.Add($"技術値が日英案の両方に保持されていません: {technicalValue}");
            }
        }

        var boundaryViolations = boundaryContext is null
            ? Array.Empty<string>()
            : FindDataBoundaryViolations(pair, attachmentNames, boundaryContext);
        if (boundaryViolations.Count > 0)
        {
            errors.Add($"メーカー向け案に許可されていない情報が含まれています: {string.Join(", ", boundaryViolations)}");
        }

        return new ManufacturerDraftValidationResult
        {
            Succeeded = errors.Count == 0,
            QuestionParity = questionParity,
            AttachmentParity = attachmentParity,
            TechnicalParity = technicalParity,
            JapaneseQuestionNumbers = japaneseQuestions,
            EnglishQuestionNumbers = englishQuestions,
            MissingJapaneseProtectedValues = missingJapaneseProtectedValues,
            MissingEnglishProtectedValues = missingEnglishProtectedValues,
            DataBoundary = boundaryViolations.Count == 0,
            DataBoundaryViolations = boundaryViolations,
            Errors = errors,
        };
    }

    public static ManufacturerProtectedValueSet CreateCanonicalProtectedValueSet(
        string productName,
        string supportId,
        string artifactFileName,
        IReadOnlyList<string> attachmentNames,
        params string[] sourceTexts)
    {
        var items = new List<ManufacturerProtectedValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string literal, string category, string source)
        {
            if (!string.IsNullOrWhiteSpace(literal) && seen.Add(literal))
            {
                items.Add(new ManufacturerProtectedValue
                {
                    Literal = literal,
                    Category = category,
                    Source = source,
                });
            }
        }

        foreach (var value in ExtractProtectedTechnicalValues(sourceTexts))
        {
            Add(value, "Technical", "CurrentCase");
        }

        foreach (var fileName in attachmentNames.Where(static name => !string.IsNullOrWhiteSpace(name)))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (TechnicalFileStem.IsMatch(stem))
            {
                Add(stem, "Technical", "CurrentCase");
            }
        }

        Add(productName, "Technical", "CurrentCase");
        Add(supportId, "Technical", "CurrentCase");
        Add(artifactFileName, "Attachment", "ArtifactPreview");
        return new ManufacturerProtectedValueSet { Items = items };
    }

    public static IReadOnlyList<string> ExtractProtectedTechnicalValues(params string[] sourceTexts)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceText in sourceTexts.Where(static text => !string.IsNullOrWhiteSpace(text)))
        {
            foreach (var pattern in TechnicalValuePatterns)
            {
                foreach (Match match in pattern.Matches(sourceText))
                {
                    if (!NonCustomerFacingMetadata.Contains(match.Value))
                    {
                        values.Add(match.Value);
                    }
                }
            }

            foreach (var phrase in new[] { "Path Traversal", "CxAudit", "CxQL Query" })
            {
                if (sourceText.Contains(phrase, StringComparison.Ordinal))
                {
                    values.Add(phrase);
                }
            }
        }

        return values.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static ManufacturerDraftParseResult Failure(string error) => new()
    {
        Errors = [error],
    };

    private static IReadOnlyList<string> FindDataBoundaryViolations(
        ManufacturerDraftPair pair,
        IReadOnlyList<string> attachmentNames,
        ManufacturerDraftBoundaryContext boundaryContext)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        var drafts = new[] { pair.JapaneseDraft, pair.EnglishDraft };
        foreach (var draft in drafts)
        {
            if (!string.IsNullOrWhiteSpace(boundaryContext.CustomerPersonName)
                && draft.Contains(boundaryContext.CustomerPersonName, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add("CustomerPersonName");
            }

            if (!string.IsNullOrWhiteSpace(boundaryContext.CustomerCompanyName)
                && draft.Contains(boundaryContext.CustomerCompanyName, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add("CustomerCompanyName");
            }

            if (Regex.IsMatch(draft, @"\b[^\s<>]+@[^\s<>]+\b", RegexOptions.CultureInvariant))
            {
                violations.Add("CustomerEmail");
            }

            if (Regex.IsMatch(draft, @"(?:内線|extension|ext\.?|内線番号)\s*[:：]?\s*\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                violations.Add("CustomerExtension");
            }

            if (Regex.IsMatch(draft, @"(?:電話|tel\.?|phone|携帯|mobile|fax)\s*[:：]?\s*[+()\d][\d()\-\s]{5,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                violations.Add("CustomerPhone");
            }

            foreach (var marker in new[]
                     {
                         "CurrentCase Evidence",
                         "selected CurrentCase Evidence",
                         "selected evidence",
                         "translation target",
                         "target elements",
                         "artifact generation",
                         "protected value",
                         "Internal Runtime",
                         "Source Role",
                         "GeneratedArtifact",
                         "RAG",
                         "LLM",
                         "Prompt",
                         "validator",
                         "parser",
                     })
            {
                var pattern = marker.Any(static character => !char.IsLetterOrDigit(character))
                    ? $"(?<!\\w){Regex.Escape(marker)}(?!\\w)"
                    : $"\\b{Regex.Escape(marker)}\\b";
                if (Regex.IsMatch(
                        draft,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    violations.Add($"Internal:{marker}");
                }
            }

            var attachmentTokens = ExtractAttachmentFileNames(draft);
            foreach (var fileName in attachmentTokens)
            {
                if (!attachmentNames
                        .Select(NormalizeAttachmentName)
                        .Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add($"Attachment:{fileName}");
                }
            }

            foreach (var prohibited in boundaryContext.ProhibitedAttachmentNames
                         .Where(static name => !string.IsNullOrWhiteSpace(name)))
            {
                var normalizedProhibited = NormalizeAttachmentName(prohibited);
                if (attachmentTokens.Contains(normalizedProhibited))
                {
                    violations.Add($"PriorAttachment:{prohibited}");
                }
            }
        }

        return violations.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

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

    private static string UnwrapJson(string response)
    {
        var trimmed = response.Trim();
        const string fence = "\u0060\u0060\u0060";
        if (!trimmed.StartsWith(fence, StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var closingFence = trimmed.LastIndexOf(fence, StringComparison.Ordinal);
        if (firstNewline < 0 || closingFence <= firstNewline)
        {
            return trimmed;
        }

        return trimmed[(firstNewline + 1)..closingFence].Trim();
    }

    private static int[] ExtractQuestionNumbers(string text, Regex pattern)
    {
        return pattern.Matches(text)
            .Select(static match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(static number => number)
            .ToArray();
    }
}
