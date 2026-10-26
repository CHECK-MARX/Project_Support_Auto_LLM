using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Quality;

public static partial class AnswerQualityEvaluator
{
    // Transient directed propositions, not an additional fact store. Both endpoints
    // and the relation must be supported by one original source span. Co-occurrence,
    // chronological observations and chains assembled across sources are not proof.
    private sealed record Relation(string Kind, string From, string To, string Span,
        bool Negative, bool Tentative);

    private static IEnumerable<UnsupportedTechnicalClaim> FindUnsupportedRelationships(
        AnswerQualityEvaluationInput input, IReadOnlyList<AnswerQualityEvidence> caseEvidence,
        TopicEntityCatalog catalog)
    {
        var primary = input.Evidence.Where(source => source.SourceType is "OfficialDoc" or "Manual" or "Manufacturer")
            .Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
            .Where(source => SourceMatchesInquiry(input.Question, source.Text, source.SourceType, catalog)).ToArray();
        foreach (var claim in ExtractRelationships(input.Answer))
        {
            var attributed = Regex.IsMatch(claim.Span, @"顧客|お客様|申告|案件履歴|案件記録|添付ログ|観測");
            var promoted = Regex.IsMatch(claim.Span, @"公式(?:資料|仕様)|製品仕様|仕様上|正式|必ず|保証");
            var supported = primary.Any(source => ExtractRelationships(source.Text).Any(proof => RelationshipMatches(claim, proof)));
            if (!supported && attributed && !promoted)
            {
                supported = caseEvidence.Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
                    .Any(source => (!source.Text.Contains("メーカー原文未確認", StringComparison.Ordinal) ||
                            Regex.IsMatch(claim.Span, @"メーカー.{0,12}原文.{0,12}未確認")) &&
                        ExtractRelationships(source.Text).Any(proof => RelationshipMatches(claim, proof))) ||
                    ExtractRelationships(input.Question).Any(proof => RelationshipMatches(claim, proof));
            }
            if (!supported)
                yield return new UnsupportedTechnicalClaim
                {
                    Kind = "EvidenceRelationship", Value = claim.Span.Trim(), IsMajor = true,
                };
        }
    }

    private static IEnumerable<Relation> ExtractRelationships(string text)
    {
        foreach (var original in Regex.Split(text, @"[。！？!?\r\n]+|(?<=\.)(?!\d)\s+"))
        {
            var sentence = Regex.Replace(original.Trim(), @"^.*?(?:事実|事項|確認|理由)[:：]\s*", "");
            sentence = StripRelationAttribution(sentence);
            if (sentence.Length == 0) continue;
            // Delimit a new clause before extracting its subject. A pronoun without
            // its own bound antecedent is deliberately not accepted as direct proof.
            foreach (var clause in Regex.Split(sentence, @"[、,]\s*(?=それ|また|一方|ただし)|[；;]"))
            {
                // Restrict uncertainty to this clause, not an entire paragraph. A
                // separate unknown must not hide an earlier affirmative relationship.
                if (IsRelationshipQuestion(clause)) continue;
                var patterns = new (string Kind, string Pattern)[]
                {
                    ("Cause", @"(?<from>.+?)(?:によって|により|による(?!と)|の結果[、,]?)(?<to>.+)"),
                    ("Cause", @"(?<from>.+?)(?:が|は)(?<to>.+?)の原因(?:で|と|に).*$"),
                    ("Cause", @"(?<to>.+?)の原因は(?<from>.+?)(?:です|である|と考え).*$"),
                    ("Cause", @"(?<from>.+?)(?:のため|(?:である|ある|いる|ない|なかった|した|する|された)ため)[、,]?(?<to>.+)"),
                    ("Contributes", @"(?<from>.+?)が(?<to>.+?)に関与(?:し|する|した).*$"),
                    ("Contributes", @"(?<from>.+?)が関与(?:し|する|した)(?<to>.*)$"),
                    ("Requires", @"(?<to>.+?)(?:には|に)(?<from>.+?)(?:が必要|を必要と|が必須).*$"),
                    ("Requires", @"(?<to>.+?)(?:は|が)(?<from>.+?)(?:に依存|を前提).*$"),
                    ("Cause", @"(?<to>.+?)\s+(?:is |was )?(?:caused by|due to|results? from)\s+(?<from>.+)"),
                    ("Cause", @"(?<from>.+?)\s+(?:(?:may|might|could)\s+)?(?:causes?|caused|triggers?|triggered|changes?|changed|leads? to|results? in)\s+(?<to>.+)"),
                    ("Requires", @"(?<to>.+?)\s+(?:(?:may|might|could)\s+)?(?:requires?|depends? on|needs?)\s+(?<from>.+)"),
                    // Atomic restrictions and role bindings are propositions in their
                    // own right. Their endpoints must occur in this predicate, rather
                    // than be assembled from separately mentioned technical terms.
                    ("RestrictedTo", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)に限定(?<predicate>されていない|されていません|されない|されている|される|されます)"),
                    ("RestrictedTo", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)(?:でのみ|のみである|のみです|のみとなる|のみとなります)"),
                    ("RequiresRole", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)(?:である|になる)必要(?<predicate>がない|はない|がある|があります|です)"),
                    ("RequiresRole", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)でなければ(?<predicate>ならない|なりません)"),
                    ("UsableIn", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)(?:では|で|には)(?<predicate>使用できない|使用できません|利用できない|利用できません|使用できる|使用できます|利用できる|利用できます)"),
                    ("RoleBinding", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)の役割を(?<predicate>担わない|担いません|担う|担います|担っている)"),
                    ("AssignedTo", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)(?:へ|に)(?<predicate>割り当てられない|割り当てられていない|割り当てられる|割り当てられています|割り当てられている)"),
                    ("AppliesTo", @"(?<from>[^、,；;]+?)(?:は|が)(?<to>[^、,；;]+?)に(?<predicate>適用されない|適用されていない|適用される|適用されます|適用されている)"),
                    ("RestrictedTo", @"(?<from>.+?)\s+(?:(?:may|might|could)\s+)?(?:is |be |are )?(?<predicate>not )?restricted to\s+(?<to>[^,;]+)"),
                    ("RequiresRole", @"(?<from>.+?)\s+(?:must|needs? to)\s+be\s+(?<to>[^,;]+)"),
                    ("UsableIn", @"(?<from>.+?)\s+(?<predicate>cannot|can not|can)\s+be used (?:on|in|with)\s+(?<to>[^,;]+)"),
                    ("AssignedTo", @"(?<from>.+?)\s+(?:(?:may|might|could)\s+)?(?:is |be |are )?(?<predicate>not )?assigned to\s+(?<to>[^,;]+)"),
                };
                foreach (var (kind, pattern) in patterns)
                {
                    var match = Regex.Match(clause, pattern, RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    var from = match.Groups["from"].Value.Trim(' ', '、', ',');
                    var to = match.Groups["to"].Value.Trim(' ', '、', ',');
                    // Asking for logs in order to investigate is a request rationale,
                    // not a product/component dependency or a technical causal claim.
                    if (Regex.IsMatch(from, @"(?:確認|調査|特定|判断|照合|把握)(?:する)?$|明確に(?:する)?$|^するため")) continue;
                    // A comparison of the available records motivating a request to
                    // inspect them is an epistemic review rationale, not a product
                    // restriction or a cause of changed product behaviour. Any
                    // independently asserted restriction is still extracted below.
                    if (kind == "Cause" &&
                        Regex.IsMatch(from, @"(?:根拠|資料|記録)(?:間|との?|と|に).*(?:差異|相違|違い|不足)(?:が|は)?$") &&
                        Regex.IsMatch(to, @"^[^。]*(?:を確認してください|をご提供ください|を照合してください)$")) continue;
                    // The connector asserts the relation regardless of the vocabulary
                    // used for the effect (including a novel or negative effect).
                    // Do not gate causal validation on a list of known event verbs.
                    var predicate = match.Groups["predicate"];
                    yield return new Relation(kind, from, to, original,
                        kind == "RequiresRole" && predicate.Value is "ならない" or "なりません"
                            ? false // "でなければならない" asserts an affirmative requirement.
                            : kind is "RestrictedTo" or "RequiresRole" or "UsableIn" or "RoleBinding" or "AssignedTo" or "AppliesTo"
                            ? Regex.IsMatch(predicate.Value, @"ない|いません|わない|cannot|can not|\bnot\b", RegexOptions.IgnoreCase)
                            : Regex.IsMatch(clause, @"原因では(?:ない|ありません)|依存し(?:ない|ません)|(?:変更|発生)し(?:ない|ません)|\b(?:not|never)\b", RegexOptions.IgnoreCase),
                        Regex.IsMatch(clause, @"可能性|考えられ|かもしれ|\b(?:may|might|could|possibly)\b", RegexOptions.IgnoreCase));
                    // Do not stop at the first relation: a restriction and a causal
                    // conclusion drawn from it each require their own direct proof.
                }
            }
        }
    }

    private static bool IsRelationshipQuestion(string clause) =>
        Regex.IsMatch(clause, @"(?:でしょうか|ですか|ますか)$|かどうか(?:を|は)?(?:確認|判断|断定)|かどうか.{0,12}(?:確認でき|不明)|(?:か否か|とは|であるか|されるか|されているか).{0,12}(?:確認|判断|断定)でき(?:ない|ません)") ||
        !Regex.IsMatch(clause, @"可能性が(?:あり|ある)|考えられ|かもしれ|\b(?:may|might|could)\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(clause, @"かどうか|か否か|(?:原因|必要|変更された|発生した)(?:かは|かを|であるか)|(?:関係|関係性|因果関係)は.{0,25}(?:確認でき|不明|断定でき)|(?:でしょうか|ですか|ますか)$|\b(?:whether|cannot confirm|unknown whether)\b", RegexOptions.IgnoreCase);

    private static bool RelationshipMatches(Relation claim, Relation proof)
    {
        if (claim.Kind != proof.Kind || claim.Negative != proof.Negative ||
            (!claim.Tentative && proof.Tentative)) return false;
        if (!RelationEndpointMatches(claim.From, proof.From) || !RelationEndpointMatches(claim.To, proof.To)) return false;
        // Conditions belong to the relation, not merely to a bag of supported words.
        // Dropping a version, timing or prerequisite broadens a conditional fact.
        return Regex.Matches(proof.Span, @"(?<![\d.])\d+(?:\.\d+){1,3}(?![\d.])")
                .All(version => HasAnchor(claim.Span, version.Value)) &&
            RelationConditions(proof.Span).All(condition =>
                NormalizeRelationText(claim.Span).Contains(condition, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> RelationConditions(string span) =>
        Regex.Matches(span, @"[^、,。;；:：]+?(?:の場合|のとき|条件で|前に|後に|に限り)|\b(?:if|when|before|after|only for)\b[^,.;]+", RegexOptions.IgnoreCase)
            .Select(match => NormalizeRelationText(match.Value));

    private static bool RelationEndpointMatches(string claim, string proof)
    {
        var left = NormalizeRelationText(claim);
        var right = NormalizeRelationText(proof);
        if (left.Length < 2 || right.Length < 2 ||
            new[] { left, right }.Any(endpoint => Regex.IsMatch(endpoint,
                @"^(?:それ|これ|そのこと)(?:$|の|が|は)|^(?:it|this)\b", RegexOptions.IgnoreCase))) return false;
        // Bind role-local identifiers, not identifiers anywhere in the document.
        var leftAnchors = BindingAnchors(left); var rightAnchors = BindingAnchors(right);
        if (leftAnchors.Any(anchor => !HasAnchor(right, anchor)) || rightAnchors.Any(anchor => !HasAnchor(left, anchor))) return false;
        // A matching subphrase cannot justify additional actors or a broader effect.
        // Unresolved paraphrases fail closed instead of inventing a semantic link.
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelationText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).Trim();
        normalized = StripRelationAttribution(normalized);
        normalized = Regex.Replace(normalized, @"(?:可能性が(?:あります|あり|ある)|と考えられます|かもしれません|と(?:の)?(?:申告|報告|記録)があります|です|である|されています|されている|されました|された|します|した|する)[\s.]*$", "");
        normalized = Regex.Replace(normalized, @"(?:が|は|を|に|の)$|\s+|[、,「」『』]", "");
        return normalized;
    }

    private static string StripRelationAttribution(string text) => Regex.Replace(text,
        @"^(?:(?:顧客|お客様)(?:の申告では|からの申告では|によると)|案件(?:履歴|記録)(?:では|には|によると)|(?:公式資料|マニュアル)(?:では|によると|により)|製品仕様上)[、,\s]*", "");
}
