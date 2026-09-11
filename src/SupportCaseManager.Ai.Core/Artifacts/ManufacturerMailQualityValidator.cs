using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Artifacts;

public sealed record ManufacturerMailQualityResult
{
    public bool Succeeded { get; init; }
    public IReadOnlyList<string> MissingSections { get; init; } = [];
    public IReadOnlyList<string> ForbiddenContent { get; init; } = [];
    public ManufacturerMailSemanticResult Semantic { get; init; } = new();
    public IReadOnlyList<string> Issues => MissingSections
        .Concat(ForbiddenContent)
        .Concat(Semantic.Issues)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>Checks formal-mail structure separately from data-boundary validation.</summary>
public sealed class ManufacturerMailQualityValidator
{
    private static readonly string[] ForbiddenPhrases =
    [
        "CurrentCase Evidence",
        "selected CurrentCase Evidence",
        "Source Role",
        "GeneratedArtifact",
        "translation target",
        "translation_target",
        "target elements",
        "Internal Runtime",
        "RAG",
        "LLM",
        "validator",
        "parser",
        "ファイル内容を確認できません",
        "ファイル内容は確認できません",
        "各ファイル内容は確認できません",
        "各ファイル内容は未確認です",
        "翻訳対象要素数",
        "CurrentCase Evidenceがありません",
        "not yet verified",
        "not confirmed",
        "未確認です",
    ];

    public ManufacturerMailQualityResult Validate(
        ManufacturerDraftPair pair,
        ManufacturerSafeContext safe)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(safe);

        var missing = new List<string>();
        var japanese = pair.JapaneseDraft;
        var english = pair.EnglishDraft;
        var followUp = safe.DraftMode == ManufacturerDraftMode.FollowUp;
        var brief = ManufacturerMailBriefBuilder.Build(safe);
        var japaneseSubject = Regex.Match(japanese, @"(?m)^\s*件名\s*[:：].*$").Value;
        var englishSubject = Regex.Match(english, @"(?im)^\s*subject\s*:.*$").Value;

        Require(missing, "Japanese.Subject", Regex.IsMatch(japanese, @"(?m)^\s*件名\s*[:：]"));
        Require(missing, "Japanese.SubjectSupportId", string.IsNullOrWhiteSpace(safe.SupportId)
            || japaneseSubject.Contains(safe.SupportId, StringComparison.Ordinal));
        Require(missing, "Japanese.Recipient", ContainsAny(japanese, "ご担当者様", "メーカーサポート", "様"));
        Require(missing, "Japanese.ResolvedRecipient", !safe.ManufacturerRecipient.IsResolved
            || japanese.Contains(safe.ManufacturerRecipient.DisplayName, StringComparison.OrdinalIgnoreCase));
        Require(missing, "Japanese.Greeting", japanese.Contains("お世話になっております", StringComparison.Ordinal));
        Require(missing, "Japanese.ToyoIntroduction", ContainsAll(japanese, "東陽テクニカ", "伊藤"));
        Require(missing, "Japanese.PriorResponseThanks", !followUp || ContainsAll(japanese, "ご回答", "ありがとう"));
        Require(missing, "Japanese.FollowUpTransition", !followUp || ContainsAny(japanese, "追加確認", "追加の確認", "追加質問"));
        Require(missing, "Japanese.CurrentAttachment", ContainsAll(japanese, safe.CurrentOutboundAttachments));
        Require(
            missing,
            "Japanese.RelevantPriorResponse",
            !followUp || (!HasPriorMaterial(safe) || ContainsAny(japanese, "前回", "ご回答", "回答")));
        Require(
            missing,
            "Japanese.CustomerIntent",
            safe.CurrentCustomerDeltaTechnicalContent.Count == 0
                || ContainsAny(japanese, "お客様", "ご要望", "希望", "維持", "実装方針"));
        Require(
            missing,
            "Japanese.TechnicalQuestionContext",
            brief.MajorTechnicalTopics.Count == 0
                || brief.MajorTechnicalTopics.All(topic => japanese.Contains(topic, StringComparison.Ordinal)));
        Require(missing, "Japanese.Closing", japanese.Contains("よろしくお願いいたします", StringComparison.Ordinal));
        Require(missing, "Japanese.ToyoSignature", ContainsAll(japanese, "株式会社東陽テクニカ", "伊藤"));

        Require(missing, "English.Subject", Regex.IsMatch(english, @"(?im)^\s*subject\s*:"));
        Require(missing, "English.SubjectSupportId", string.IsNullOrWhiteSpace(safe.SupportId)
            || englishSubject.Contains(safe.SupportId, StringComparison.Ordinal));
        Require(missing, "English.Recipient", ContainsAny(english, "Hello Support Team", "Hi ", "Dear "));
        Require(missing, "English.ResolvedRecipient", !safe.ManufacturerRecipient.IsResolved
            || english.Contains(safe.ManufacturerRecipient.DisplayName, StringComparison.OrdinalIgnoreCase));
        Require(missing, "English.Greeting", ContainsAny(english, "Hello ", "Hi ", "Dear "));
        Require(missing, "English.ToyoIntroduction", ContainsAll(english, "Ken Ito", "Toyo"));
        Require(missing, "English.PriorResponseThanks", !followUp || ContainsAll(english, "Thank you", "previous"));
        Require(missing, "English.FollowUpTransition", !followUp || ContainsAny(english, "additional", "follow-up", "follow up"));
        Require(missing, "English.CurrentAttachment", ContainsAll(english, safe.CurrentOutboundAttachments));
        Require(
            missing,
            "English.RelevantPriorResponse",
            !followUp || (!HasPriorMaterial(safe) || ContainsAny(english, "previous", "response", "reply")));
        Require(
            missing,
            "English.CustomerIntent",
            safe.CurrentCustomerDeltaTechnicalContent.Count == 0
                || ContainsAny(english, "customer", "would like", "prefer", "maintain", "requested"));
        Require(
            missing,
            "English.TechnicalQuestionContext",
            brief.MajorTechnicalTopics.Count == 0
                || brief.MajorTechnicalTopics.All(topic => english.Contains(topic, StringComparison.Ordinal)));
        Require(missing, "English.Closing", ContainsAny(english, "Best regards", "Kind regards", "Sincerely"));
        Require(missing, "English.ToyoSignature", ContainsAll(english, "Ken Ito", "Toyo"));

        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in ForbiddenPhrases)
        {
            if (ContainsAny(pair.JapaneseDraft, phrase) || ContainsAny(pair.EnglishDraft, phrase))
            {
                forbidden.Add($"Forbidden:{phrase}");
            }
        }

        var semantic = new ManufacturerMailSemanticValidator().Validate(pair, brief);
        return new ManufacturerMailQualityResult
        {
            Succeeded = missing.Count == 0 && forbidden.Count == 0 && semantic.Succeeded,
            Semantic = semantic,
            MissingSections = missing,
            ForbiddenContent = forbidden.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
        };
    }

    private static bool HasPriorMaterial(ManufacturerSafeContext safe) =>
        safe.RelevantPriorManufacturerResponse.Count > 0
        || !string.IsNullOrWhiteSpace(safe.PriorTechnicalContext);

    private static bool ContainsAll(string value, IEnumerable<string> candidates) =>
        candidates.All(candidate => !string.IsNullOrWhiteSpace(candidate)
            && value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAll(string value, params string[] candidates) =>
        candidates.All(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static void Require(ICollection<string> missing, string name, bool condition)
    {
        if (!condition)
        {
            missing.Add(name);
        }
    }
}
