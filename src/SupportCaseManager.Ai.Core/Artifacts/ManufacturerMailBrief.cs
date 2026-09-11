using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ManufacturerMailBriefPoint(string Id, string Text, IReadOnlyList<string> Concepts);

public sealed record ManufacturerMailBrief
{
    public ManufacturerDraftMode Mode { get; init; }
    public string SupportId { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string ProductVersion { get; init; } = string.Empty;
    public ManufacturerRecipient Recipient { get; init; } = new();
    public string OriginRole { get; init; } = "CUSTOMER";
    public string RequestSource { get; init; } = "CUSTOMER_QUESTION";
    public string RecipientRole { get; init; } = "MANUFACTURER";
    public string Purpose { get; init; } = string.Empty;
    public string FollowUpTrigger { get; init; } = "NONE";
    public bool CloseRequested { get; init; }
    public string CloseIntentSourceFileName { get; init; } = string.Empty;
    public string CurrentCustomerDeltaSourceFileName { get; init; } = string.Empty;
    public ManufacturerCaseStage CaseStage { get; init; }
    public IReadOnlyList<ManufacturerMailBriefPoint> PriorResponseSummaryPoints { get; init; } = [];
    public bool PreviousManufacturerContactConfirmed { get; init; }
    public bool PreviousCustomerReplyFound { get; init; }
    public IReadOnlyList<ManufacturerMailBriefPoint> CurrentCustomerIntent { get; init; } = [];
    public IReadOnlyList<ManufacturerMailBriefPoint> CurrentQuestions { get; init; } = [];
    public IReadOnlyList<string> RequiredTechnicalLiterals { get; init; } = [];
    public IReadOnlyList<string> MailBodyRequiredLiterals { get; init; } = [];
    public IReadOnlyList<string> MailBodyOrAttachmentRequiredLiterals { get; init; } = [];
    public IReadOnlyList<string> MajorTechnicalTopics { get; init; } = [];
    public IReadOnlyList<string> CurrentOutboundAttachments { get; init; } = [];
    public IReadOnlyList<string> OptionalAdditionalMaterialOffer { get; init; } = [];
    public IReadOnlyList<string> ForbiddenClaims { get; init; } = [];
    public bool Ready => CurrentQuestions.Count > 0
        && CurrentQuestions.All(point => point.Concepts.Count > 0)
        && (Mode != ManufacturerDraftMode.FollowUp
            || PriorResponseSummaryPoints.Count > 0
            || (PreviousManufacturerContactConfirmed && PreviousCustomerReplyFound));
    public bool IsAttachmentCentricFollowUp => Mode == ManufacturerDraftMode.FollowUp
        && CurrentQuestions.Count > 0 && CurrentOutboundAttachments.Count > 0;
}

public static class ManufacturerMailBriefBuilder
{
    public static ManufacturerMailBrief Build(ManufacturerSafeContext safe)
    {
        // Only role-scoped, sanitized customer text can define the questions.
        // Generated answers and requested task text are not authoritative sources.
        var delta = Sentences(safe.CurrentCustomerDeltaTechnicalContent).ToArray();
        var questions = delta.Where(text => Regex.IsMatch(text,
            @"質問\s*\d|Question\s*\d|[?？]|ご教示|教えて|確認したい|確認してください|please\s+(?:confirm|explain)|could\s+you",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
        var intents = delta.Except(questions).Where(text => Regex.IsMatch(text,
            @"希望|維持|コスト|修正範囲|たい|要望|would like|prefer|maintain|cost|wish|want",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
        var questionConcepts = questions.SelectMany(ManufacturerMailConcepts.Extract).ToHashSet(StringComparer.Ordinal);
        var prior = Sentences(safe.RelevantPriorManufacturerResponse)
            .Concat(Sentences([safe.PriorTechnicalContext]))
            .Where(text => ManufacturerMailConcepts.Extract(text).Any(questionConcepts.Contains))
            .Distinct(StringComparer.Ordinal)
            .Take(3).ToArray();
        var sourceText = string.Join("\n", delta.Concat(prior));
        var majorTopics = ManufacturerMailConcepts.MajorLiterals
            .Where(value => sourceText.Contains(value, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        var bodyRequired = safe.CurrentOutboundAttachments
            .Concat(majorTopics)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var bodyOrAttachment = safe.RequiredProtectedTechnicalValues
            .Except(bodyRequired, StringComparer.OrdinalIgnoreCase).ToArray();
        var prohibited = ManufacturerMailConcepts.TopicPatterns.Keys
            .Where(topic => !ManufacturerMailConcepts.HasTopic(string.Join("\n", delta), topic)).ToArray();
        return new ManufacturerMailBrief
        {
            Mode = safe.DraftMode,
            Product = safe.ProductName,
            ProductVersion = safe.ProductVersion,
            SupportId = safe.SupportId,
            Recipient = safe.ManufacturerRecipient,
            OriginRole = "CUSTOMER",
            RequestSource = safe.DraftMode == ManufacturerDraftMode.FollowUp
                ? "CUSTOMER_ADDITIONAL_QUESTION" : "CUSTOMER_QUESTION",
            RecipientRole = "MANUFACTURER",
            Purpose = safe.DraftMode == ManufacturerDraftMode.FollowUp
                ? "FOLLOW_UP_TO_MANUFACTURER_RESPONSE" : "INITIAL_REQUEST_TO_MANUFACTURER",
            FollowUpTrigger = safe.DraftMode == ManufacturerDraftMode.FollowUp
                ? "CUSTOMER_ADDITIONAL_QUESTIONS" : "NONE",
            CloseRequested = delta.Any(ManufacturerMailConcepts.HasCloseRequest),
            CloseIntentSourceFileName = delta.Any(ManufacturerMailConcepts.HasCloseRequest)
                ? safe.CurrentCustomerDeltaSourceFileName : string.Empty,
            CurrentCustomerDeltaSourceFileName = safe.CurrentCustomerDeltaSourceFileName,
            CaseStage = safe.CaseStage,
            CurrentQuestions = Points("Q", questions),
            CurrentCustomerIntent = Points("I", intents),
            PriorResponseSummaryPoints = Points("P", prior),
            PreviousManufacturerContactConfirmed = safe.PreviousManufacturerContactConfirmed,
            PreviousCustomerReplyFound = safe.PreviousCustomerReplyFound,
            CurrentOutboundAttachments = safe.CurrentOutboundAttachments,
            RequiredTechnicalLiterals = safe.RequiredProtectedTechnicalValues
                .Where(value => sourceText.Contains(value, StringComparison.Ordinal)
                    || value == safe.SupportId || value == safe.ProductName
                    || safe.CurrentOutboundAttachments.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray(),
            MailBodyRequiredLiterals = bodyRequired,
            MailBodyOrAttachmentRequiredLiterals = bodyOrAttachment,
            MajorTechnicalTopics = majorTopics,
            ForbiddenClaims = prohibited,
        };
    }

    private static ManufacturerMailBriefPoint[] Points(string prefix, IEnumerable<string> values) => values
        .Distinct(StringComparer.Ordinal).Select((text, index) =>
            new ManufacturerMailBriefPoint($"{prefix}{index + 1}", text, ManufacturerMailConcepts.Extract(text))).ToArray();

    private static IEnumerable<string> Sentences(IEnumerable<string> texts) => texts
        .SelectMany(text => Regex.Split(text, @"\r?\n|(?<=[。？！?])\s*"))
        .Select(text => text.Trim()).Where(text => text.Length > 3
            && !text.Contains("[non-current attachment omitted]", StringComparison.Ordinal)
            && !text.Contains("[customer", StringComparison.Ordinal)
            && !text.Contains("[local path omitted]", StringComparison.Ordinal));
}

// Bilingual concept coverage is conservative. Unknown questions are held for review,
// rather than treating matching question numbers as proof of semantic equivalence.
public static class ManufacturerMailConcepts
{
    public static bool HasCloseRequest(string text) => Regex.Split(text, @"[。！？!?\r\n]")
        .Any(sentence => Regex.IsMatch(sentence,
            @"(?:close|closing|closure of)\s+(?:(?:this|the|our|support)\s+)*(?:case|ticket)|(?:案件|ケース|チケット|本件).{0,24}(?:クローズ|終了)|(?:クローズ|終了).{0,12}(?:希望|したい|してください)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(sentence,
                @"\b(?:do not|don't|not|never)\s+(?:(?:want|wish|intend|plan|like)\s+)?(?:to\s+)?(?:close|end)\b|(?:クローズ|終了).{0,16}(?:しない|しません|不要|希望しない|望まない)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public static bool HasQuestionRequest(string text) => Regex.IsMatch(text,
        @"(?:質問\s*\d*|Question\s*\d*|\?|？|could\s+you|please\s+(?:confirm|clarify|review|explain)|ご確認(?:ください|いただ)|教えて(?:ください|いただ)|ご教示)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> MajorLiterals { get; } =
    [
        "GetSecurePath", "GetSecureName", "GetSecureFileName",
        "IsCorrectPath", "basePath", "combinePath",
    ];
    private static readonly Dictionary<string, string> Patterns = new()
    {
        ["parameters"] = @"parameter|パラメータ|引数",
        ["preserve-design"] = @"維持|maintain|retain|preserv",
        ["change-cost"] = @"コスト|修正範囲|cost|scope of (?:the )?changes",
        ["sanitizer"] = @"sanitiz|サニタイザ",
        ["implementation"] = @"実装|implement",
        ["example"] = @"実装例|コード例|example|sample",
        ["combine"] = @"結合|combin|concatenat",
        ["after"] = @"後|then|after|before.*Path\.GetFileName",
        ["control-flow"] = @"control.flow|制御フロー",
        ["boolean"] = @"boolean|bool\b|真偽値",
        ["recognition"] = @"認識|recogniz|recognis|recognition",
        ["automatic"] = @"自動|automatic",
        ["customization"] = @"カスタマイズ|customiz|customis",
        ["not-required"] = @"不要|なし|without|not (?:required|necessary)|no need",
        ["not-recommended"] = @"推奨されない|推奨しない|not recommended|do not recommend",
        ["insufficient"] = @"不十分|十分.*(?:ない|なく)|insufficient|not sufficient",
        ["same-value"] = @"同じ値|同一.*値|same value",
        ["different-value"] = @"別.*値|異なる値|different values|distinct values",
        ["validation"] = @"検証|validat",
        ["dynamic"] = @"(?<!自)動的|dynamic",
        ["filename"] = @"ファイル名|file.?name",
        ["directory"] = @"フォルダ|ディレクトリ|folder|director",
    };
    public static IReadOnlyDictionary<string, string> TopicPatterns { get; } = new Dictionary<string, string>
    {
        ["close-intent"] = @"(?:close|closing|closure of)\s+(?:(?:this|the|our|support)\s+)*(?:case|ticket)|(?:案件|ケース|チケット|本件).{0,24}(?:クローズ|終了)|(?:クローズ|終了).{0,12}(?:希望|したい|してください)",
        ["deadline"] = @"納期|締切|期限が迫|deadline|timeframe|time frame|urgent",
        ["preset-exclusion"] = @"プリセット.*除外|除外.*プリセット|(?:exclud|exclusion).*preset|preset.*(?:exclud|exclusion)",
        ["initial-false-positive"] = @"誤検知.*(?:判断|判定)|(?:whether|determine).*false positive",
        ["character-checklist"] = @"許可文字|禁止文字|permitted.*characters|prohibited.*characters",
        ["canonicalization"] = @"正規化|canonicaliz|canonicalis",
        ["allowed-directory"] = @"許可ディレクトリ|allowed director",
        ["generic-information-request"] = @"どのような実装情報|what implementation (?:details|information)",
    };

    public static IReadOnlyList<string> Extract(string text)
    {
        var result = Patterns.Where(pair => Regex.IsMatch(text, pair.Value, RegexOptions.IgnoreCase))
            .Select(pair => pair.Key).ToList();
        result.AddRange(Regex.Matches(text,
            @"(?<![A-Za-z0-9_])(?:[A-Z][a-z]+(?:[A-Z][A-Za-z0-9]*)+|[a-z]+[A-Z][A-Za-z0-9]*|item|rev|CxQL|CxAudit|C#)(?![A-Za-z0-9_])|Path\.GetFileName\(\)")
            .Select(match => "literal:" + match.Value));
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool Covers(string text, string concept) => concept.StartsWith("literal:", StringComparison.Ordinal)
        ? Regex.IsMatch(text, @"(?<![A-Za-z0-9_])" + Regex.Escape(concept[8..]) + @"(?![A-Za-z0-9_])")
        : Patterns.TryGetValue(concept, out var pattern) && Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);

    public static bool HasTopic(string text, string topic) => Regex.IsMatch(text, TopicPatterns[topic], RegexOptions.IgnoreCase);
}
