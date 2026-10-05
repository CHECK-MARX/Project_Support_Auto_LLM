using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Quality;

public static partial class AnswerQualityEvaluator
{
    private static readonly string[] InternalMarkers =
    [
        "[RAG Evidence]", "Evidence score", "Selection reason", "fallback",
        "TopK", "InsufficientEvidence", "CompletedWithFallback", "Phase 14",
        "Phase 15", "Phase 16", "Phase 17", "RAG内部", "内部スコア",
    ];

    private static readonly string[] InsufficientMarkers =
    [
        "根拠不足", "情報不足", "確認できません", "判断できません",
        "断定できません", "insufficient evidence",
    ];

    public static AnswerQualityEvaluationResult Evaluate(
        AnswerQualityEvaluationInput input,
        AnswerQualityThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var caseEvidence = input.Evidence.Where(static item => item.SourceType == "CurrentCase").ToArray();
        // Case observations support attributed statements only. They do not increase
        // independent product evidence coverage, grounding or customer readiness.
        input = input with { Evidence = input.Evidence.Where(static item => item.SourceType != "CurrentCase").ToArray() };
        var rules = thresholds ?? AnswerQualityThresholds.Default;
        var catalog = EnsureCatalog(input.Catalog, input.ProductName);
        var queryProfile = TopicEntityAnalyzer.Extract(input.Question, catalog);
        var answerProfile = TopicEntityAnalyzer.Extract(input.Answer, catalog);
        var topicComparison = TopicEntityAnalyzer.Compare(queryProfile, answerProfile);
        var topicAlignment = CalculateTopicAlignment(queryProfile, topicComparison);

        var answerClaims = TechnicalClaimExtractor.Extract(input.Answer, catalog);
        var supportClaims = TechnicalClaimExtractor.Extract(
            string.Join('\n', new[] { input.Question }.Concat(input.Evidence.Select(static item => item.Text))),
            catalog);
        var supportedKeys = supportClaims
            .Select(static claim => $"{claim.Kind}|{claim.NormalizedValue}")
            .ToHashSet(StringComparer.Ordinal);
        var unsupported = answerClaims
            .Where(claim => !supportedKeys.Contains($"{claim.Kind}|{claim.NormalizedValue}") &&
                !SupportsAttributedCaseClaim(claim, input.Answer, caseEvidence, catalog) &&
                !(claim.Kind == "Version" && Regex.IsMatch(input.Question,
                    $@"(?<![0-9.]){Regex.Escape(claim.NormalizedValue)}(?![0-9.])")))
            .Select(static claim => new UnsupportedTechnicalClaim
            {
                Kind = claim.Kind,
                Value = claim.Value,
                IsMajor = claim.IsMajor,
            })
            .ToList();

        unsupported.AddRange(FindUnsupportedPropositions(input, caseEvidence, catalog, unsupported));
        unsupported.AddRange(FindUnsupportedRelationships(input, caseEvidence, catalog));
        unsupported = unsupported.DistinctBy(claim => (claim.Kind, claim.Value)).ToList();
        var grounding = CalculateGrounding(input, answerClaims.Count, unsupported.Count, topicAlignment);
        var technicalFidelity = answerClaims.Count == 0
            ? 1.0
            : Clamp(1.0 - (unsupported.Count / (double)answerClaims.Count));
        var legacyRequiredCoverage = RequiredCoverage(queryProfile, input.Question);
        var legacyObservedCoverage = ObservedCoverage(input.Answer, answerClaims);
        var phase175RequiredCoverage = input.RequiredCoverage.Count > 0
            ? input.RequiredCoverage.Distinct(StringComparer.Ordinal).ToList()
            : CoverageAnalyzer.Required(input.Question, queryProfile);
        var evidenceObservedCoverage = CoverageAnalyzer.Observe(input.Evidence);
        var answerObservedCoverage = CoverageAnalyzer.Observe(input.Answer);
        var missingEvidenceCoverage = phase175RequiredCoverage
            .Where(item => !evidenceObservedCoverage.Contains(item))
            .ToList();
        var missingAnswerCoverage = phase175RequiredCoverage
            .Where(item => !answerObservedCoverage.Contains(item))
            .ToList();
        var evidenceCoverage = phase175RequiredCoverage.Count == 0
            ? 1.0
            : (phase175RequiredCoverage.Count - missingEvidenceCoverage.Count) / (double)phase175RequiredCoverage.Count;
        var answerCoverage = phase175RequiredCoverage.Count == 0
            ? 1.0
            : (phase175RequiredCoverage.Count - missingAnswerCoverage.Count) / (double)phase175RequiredCoverage.Count;
        var coverage = input.UseSeparatedCoverage
            ? answerCoverage
            : legacyRequiredCoverage.Count == 0
                ? 1.0
                : legacyRequiredCoverage.Count(item => legacyObservedCoverage.Contains(item)) / (double)legacyRequiredCoverage.Count;
        var observedCoverage = input.UseSeparatedCoverage ? answerObservedCoverage : legacyObservedCoverage;
        var directness = CalculateDirectness(input.Answer, topicAlignment, coverage);
        var actionability = CalculateActionability(queryProfile, input.Answer, observedCoverage);
        var leakageCount = InternalMarkers.Count(marker =>
            input.Answer.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var customerReadiness = CalculateCustomerReadiness(input.Answer, leakageCount);
        var conflictCount = CountConflicts(input, queryProfile, catalog);

        var blocking = new List<string>();
        var warnings = new List<string>();
        if (topicComparison.TopicConflict)
        {
            blocking.Add("TopicConflict");
        }
        if (unsupported.Any(static item => item.IsMajor))
        {
            blocking.Add("MajorUnsupportedTechnicalClaim");
        }
        if (leakageCount > 0)
        {
            warnings.Add("InternalLeakage");
        }
        if (conflictCount > 0)
        {
            warnings.Add("EvidenceConflict");
        }
        if (unsupported.Count > 0)
        {
            warnings.Add("UnsupportedTechnicalClaim");
        }
        if (input.UseSeparatedCoverage && missingEvidenceCoverage.Count > 0)
        {
            warnings.Add("MissingEvidenceCoverage");
        }
        if (input.UseSeparatedCoverage && missingAnswerCoverage.Count > 0)
        {
            warnings.Add("MissingAnswerCoverage");
        }

        var decision = Decide(
            input,
            rules,
            directness,
            grounding,
            topicAlignment,
            coverage,
            technicalFidelity,
            actionability,
            customerReadiness,
            leakageCount,
            conflictCount,
            blocking,
            missingEvidenceCoverage,
            missingAnswerCoverage);

        return new AnswerQualityEvaluationResult
        {
            Directness = Round(directness),
            Grounding = Round(grounding),
            TopicAlignment = Round(topicAlignment),
            Coverage = Round(coverage),
            EvidenceCoverage = input.UseSeparatedCoverage ? Round(evidenceCoverage) : null,
            AnswerCoverage = input.UseSeparatedCoverage ? Round(answerCoverage) : null,
            RequiredCoverage = input.UseSeparatedCoverage ? phase175RequiredCoverage : null,
            MissingEvidenceCoverage = input.UseSeparatedCoverage ? missingEvidenceCoverage : null,
            MissingAnswerCoverage = input.UseSeparatedCoverage ? missingAnswerCoverage : null,
            TechnicalFidelity = Round(technicalFidelity),
            UnsupportedClaimCount = unsupported.Count,
            UnsupportedTechnicalClaims = unsupported,
            ConflictCount = conflictCount,
            Actionability = Round(actionability),
            CustomerReadiness = Round(customerReadiness),
            InternalLeakageCount = leakageCount,
            BlockingReasons = blocking.Distinct(StringComparer.Ordinal).ToList(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            Decision = decision,
        };
    }

    public static TopicEntityCatalog CreateSupportCatalog(string? productName = null) =>
        SupportTopicCatalog.Create(productName);

    // A shared binding check for generation, citation selection and claim validation.
    // Product names and mail/attachment boilerplate alone never bind another case.
    public static bool SourceMatchesInquiry(string question, string text, string sourceType,
        TopicEntityCatalog? catalog = null)
    {
        catalog ??= new TopicEntityCatalog();
        var query = TopicEntityAnalyzer.Extract(question, catalog);
        var source = TopicEntityAnalyzer.Extract(text, catalog);
        var comparison = TopicEntityAnalyzer.Compare(query, source);
        if (comparison.TopicConflict || comparison.NoTopicMatch) return false;
        var anchors = BindingAnchors(question);
        if (sourceType is "PastCaseNote" or "PastAnswer")
            return anchors.Any(anchor => HasAnchor(text, anchor)) &&
                anchors.Where(anchor => IsSpecificIdentifier(anchor) ||
                    Regex.IsMatch(anchor, @"^[A-Z][a-z]+$|[ァ-ヴー]{4,}")).All(anchor => HasAnchor(text, anchor));
        return anchors.Length == 0 || anchors.Any(anchor => HasAnchor(text, anchor));
    }

    private static string[] BindingAnchors(string text) => Regex.Matches(text,
            @"\[[A-Z_]*RESOURCE[A-Z_]*\]|(?<!\d)\d+(?:\.\d+){1,3}(?!\d)|[A-Za-z][A-Za-z0-9_.-]{3,}|[ァ-ヴー]{4,}|[「『](?<name>[^」』]{4,100})[」』]")
        .Select(match => match.Groups["name"].Success ? match.Groups["name"].Value : match.Value)
        .Where(anchor => !Regex.IsMatch(anchor,
            @"^(?:QAC|HelixQAC|Perforce|Checkmarx|CxSAST|Klocwork|Box|Attachment|Attachments|files?|account|requires?|view|following|stored|CONFIDENTIAL|CUSTOMER_ORG|PERSON|SUPPORT_ID|ORGANIZATION|http|https|サポート|バージョン|ファイル|ライセンス|インストール|アップロード|ダウンロード|ユーザー|アカウント)$",
            RegexOptions.IgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsSpecificIdentifier(string value) =>
        Regex.IsMatch(value, @"^\d+(?:\.\d+)+$|[-_.]|[a-z][A-Z]|^[A-Z]{3,}$") &&
        !value.StartsWith("[", StringComparison.Ordinal);

    private static bool HasAnchor(string text, string anchor) =>
        Regex.IsMatch(anchor, @"^\d+(?:\.\d+)+$")
            ? Regex.IsMatch(text, @"(?<![\d.])" + Regex.Escape(anchor) + @"(?![\d.])")
            : text.Contains(anchor, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<UnsupportedTechnicalClaim> FindUnsupportedPropositions(
        AnswerQualityEvaluationInput input, IReadOnlyList<AnswerQualityEvidence> caseEvidence,
        TopicEntityCatalog catalog, IReadOnlyList<UnsupportedTechnicalClaim> tokenFailures)
    {
        var current = input.Question + "\n" + string.Join("\n", caseEvidence.Select(item => item.Text));
        var independent = input.Evidence.Where(item => item.SourceType is "Manual" or "OfficialDoc" or "Manufacturer").ToArray();
        foreach (var sentence in Regex.Split(input.Answer, @"[。！？!?\r\n]+"))
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            // Absence is a proposition, not the failure to find a matching token.
            // Even an Unknown section can assert an unproved absence of documentation.
            var absences = AbsenceAssertions(sentence).ToArray();
            foreach (var absence in absences)
            {
                var absenceSupported = independent.Any(item =>
                    SourceMatchesInquiry(input.Question, item.Text, item.SourceType, catalog) &&
                    Regex.Split(item.Text, @"[。！？!?\r\n]+|(?<=\.)(?!\d)\s+").Any(span =>
                        AbsenceAssertions(span).Any(proof => proof.Kind == absence.Kind &&
                            AbsenceSubjectAndConditionsMatch(sentence, AbsenceProofClause(span, proof.Predicate), span))));
                if (!absenceSupported)
                    yield return new UnsupportedTechnicalClaim
                    {
                        Kind = "EvidenceAbsence", Value = sentence.Trim(), IsMajor = true,
                    };
            }
            var attributedPast = Regex.IsMatch(sentence, @"過去(?:の)?(?:案件|ケース)|類似案件");
            if (!attributedPast)
            {
                var introduced = input.Evidence.Where(item => item.SourceType is "PastCaseNote" or "PastAnswer")
                    .SelectMany(item => BindingAnchors(item.Text))
                    .Where(anchor => HasAnchor(sentence, anchor) && !HasAnchor(current, anchor) &&
                        !independent.Any(item => HasAnchor(item.Text, anchor)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (introduced.Length > 0)
                    yield return new UnsupportedTechnicalClaim { Kind = "CaseAttribution",
                        Value = sentence.Trim(), IsMajor = true };
            }
            // Supported negative support statements must not be reinterpreted as
            // affirmative support. Mixed affirmative assertions still get checked.
            if (absences.Length > 0 && !Regex.IsMatch(sentence,
                    @"サポートされています|サポート対象です|可能性(?:が)?あります|可能です|示されています|更新が行われます"))
                continue;
            if (!Regex.IsMatch(sentence,
                @"仕様上|(?:公式|製品)(?:仕様|資料).{0,40}(?:示され|によ|では|確認)|サポート(?:され|対象|可能)|(?:利用|運用|更新|対応)(?:が)?可能(?:性|です|である)",
                RegexOptions.IgnoreCase) ||
                (Regex.IsMatch(sentence, @"確認でき(?:ません|ない)|断定でき(?:ません|ない)|不明|未確認|未確定|可否.{0,20}(?:確認|質問)|かどうか.{0,25}確認") &&
                 !Regex.IsMatch(sentence, @"サポートされています|サポート対象です|可能性(?:が)?あります|更新が行われます|可能です|示されています")))
                continue;
            var supported = independent.Any(item => SourceMatchesInquiry(input.Question, item.Text, item.SourceType, catalog) &&
                Regex.Split(item.Text, @"[。！？!?\r\n]+|(?<=\.)(?!\d)\s+").Any(span =>
                    PropositionMatches(sentence, span)));
            if (!supported && !tokenFailures.Any(claim => sentence.Contains(claim.Value, StringComparison.OrdinalIgnoreCase)))
                yield return new UnsupportedTechnicalClaim { Kind = "EvidenceAttribution",
                    Value = sentence.Trim(), IsMajor = true };
        }
    }

    private static IEnumerable<(string Kind, Match Predicate)> AbsenceAssertions(string sentence)
    {
        var documentContext = Regex.IsMatch(sentence,
            @"資料|仕様|マニュアル|ドキュメント|リリースノート|\b(?:documents?|documentation|manual|specification)\b",
            RegexOptions.IgnoreCase);
        var predicates = new (string Kind, string Pattern)[]
        {
            ("Documentation", @"(?:記載|明記|説明)(?:は|が|も)?(?:され(?:て)?(?:いない|いません|ない|おらず)|されず|(?:が|は)?(?:ない|なく|ありません|なし))|\b(?:not documented|undocumented)\b"),
            ("Definition", @"定義(?:は|が|も)?(?:され(?:て)?(?:いない|いません|ない|おらず)|されず|(?:が|は)?(?:ない|なく|ありません|なし))|\b(?:not defined|undefined)\b"),
            ("Existence", @"存在し(?:ない|ません)|存在(?:が|は)?(?:ない|ありません)|含まれて(?:いない|いません)|\bdoes not exist\b"),
            ("Support", @"サポート(?:は|が)?(?:され(?:て)?(?:いない|いません|ない|おらず)|されず|対象外|外です)|\b(?:not supported|unsupported)\b"),
        };
        foreach (var (kind, pattern) in predicates)
        {
            if (kind == "Existence" && !documentContext) continue;
            foreach (Match predicate in Regex.Matches(sentence, pattern, RegexOptions.IgnoreCase))
            {
                var tail = sentence[(predicate.Index + predicate.Length)..];
                // This negates knowledge of absence, or asks whether absence holds.
                // A scope qualifier alone never excuses an affirmative absence claim.
                if (Regex.IsMatch(tail,
                    @"^(?:かどうか|か否か|かを|かについて)|^(?:と|とは)(?:は)?(?:断定|確認|判断)でき(?:ない|ません)|^(?:こと)?(?:は|かは)確認でき(?:ない|ません)",
                    RegexOptions.IgnoreCase)) continue;
                yield return (kind, predicate);
            }
        }
    }

    private static string AbsenceProofClause(string span, Match predicate)
    {
        var boundaries = Regex.Matches(span[..predicate.Index],
            @"[、,；;]|\b(?:whereas|while|but|however)\b|一方|ただし", RegexOptions.IgnoreCase);
        var start = boundaries.Count == 0 ? 0 : boundaries[^1].Index + boundaries[^1].Length;
        var after = predicate.Index + predicate.Length;
        var next = Regex.Match(span[after..],
            @"[、,；;]|\b(?:whereas|while|but|however)\b|一方|ただし", RegexOptions.IgnoreCase);
        var end = next.Success ? after + next.Index : span.Length;
        return span[start..end];
    }

    private static bool AbsenceSubjectAndConditionsMatch(string claim, string proofClause, string span)
    {
        var subjects = BindingAnchors(claim)
            .Concat(Regex.Matches(claim, @"[「『](?<name>[^」』]{2,100})[」』]")
                .Select(match => match.Groups["name"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (subjects.Length == 0)
        {
            // Fail closed when there is no identifiable subject, except for an
            // explicit equivalent statement in the primary source itself.
            var body = Regex.Replace(claim.Trim(), @"^.*?(?:事実|事項)[:：]\s*", "");
            if (!proofClause.Contains(body, StringComparison.OrdinalIgnoreCase)) return false;
        }
        else if (subjects.Any(subject => !HasAnchor(proofClause, subject))) return false;
        return Regex.Matches(span, @"(?<!\d)\d+(?:\.\d+){1,3}(?!\d)")
                .All(version => HasAnchor(claim, version.Value)) &&
            (!Regex.IsMatch(span, @"\bif\b|場合|条件|限定|限り", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(claim, @"\bif\b|場合|条件|限定|限り", RegexOptions.IgnoreCase)) &&
            (!Regex.IsMatch(span, @"\bbefore\b|前に|以前", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(claim, @"\bbefore\b|前に|以前", RegexOptions.IgnoreCase));
    }

    private static bool PropositionMatches(string claim, string span)
    {
        string[][] predicates = [
            ["サポート", "supported", "supports"],
            ["更新", "アップグレード", "バージョンアップ", "upgrade", "update"],
            ["利用", "使用", "運用", "use", "operate"],
        ];
        var required = predicates.Where(group => group.Any(term => claim.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (required.Length == 0 || required.Any(group => !group.Any(term => span.Contains(term, StringComparison.OrdinalIgnoreCase)))) return false;
        if (claim.Contains("サポート", StringComparison.Ordinal) &&
            Regex.IsMatch(span, @"not supported|known to work|unsupported|サポートされない", RegexOptions.IgnoreCase)) return false;
        // Preserve explicit version, component and operation conditions within the SAME span.
        var anchors = BindingAnchors(claim).Where(IsSpecificIdentifier).ToArray();
        return anchors.All(anchor => HasAnchor(span, anchor)) &&
            Regex.Matches(span, @"(?<!\d)\d+(?:\.\d+){1,3}(?!\d)").All(version => HasAnchor(claim, version.Value)) &&
            (!Regex.IsMatch(span, @"\bif\b|場合|条件", RegexOptions.IgnoreCase) || Regex.IsMatch(claim, @"場合|条件|とき")) &&
            (!Regex.IsMatch(span, @"\bbefore\b", RegexOptions.IgnoreCase) || Regex.IsMatch(claim, @"前|before", RegexOptions.IgnoreCase));
    }

    private static bool SupportsAttributedCaseClaim(AnswerTechnicalClaim claim, string answer,
        IReadOnlyList<AnswerQualityEvidence> cases, TopicEntityCatalog catalog)
    {
        var supporting = cases.Where(item => TechnicalClaimExtractor.Extract(item.Text, catalog)
            .Any(value => value.Kind == claim.Kind && value.NormalizedValue == claim.NormalizedValue)).ToArray();
        if (supporting.Length == 0) return false;
        var sentences = Regex.Split(answer, @"[。！？!?\r\n]").Where(sentence =>
            TechnicalClaimExtractor.Extract(sentence, catalog).Any(value =>
                value.Kind == claim.Kind && value.NormalizedValue == claim.NormalizedValue)).ToArray();
        return sentences.Length > 0 && sentences.All(sentence =>
            Regex.IsMatch(sentence, @"案件履歴|案件記録|添付ログ") &&
            !Regex.IsMatch(sentence, @"正式保証|公式仕様|公式資料|正式サポート|必ず|確実に") &&
            supporting.Any(item => !item.Text.Contains("メーカー原文未確認", StringComparison.Ordinal) ||
                Regex.IsMatch(sentence, @"メーカー.{0,12}原文.{0,12}(?:未確認|確認できません|確認されていない)")));
    }

    private static string Decide(
        AnswerQualityEvaluationInput input,
        AnswerQualityThresholds rules,
        double directness,
        double grounding,
        double topicAlignment,
        double coverage,
        double technicalFidelity,
        double actionability,
        double customerReadiness,
        int leakageCount,
        int conflictCount,
        IReadOnlyCollection<string> blocking,
        IReadOnlyCollection<string> missingEvidenceCoverage,
        IReadOnlyCollection<string> missingAnswerCoverage)
    {
        if (blocking.Count > 0)
        {
            return AnswerQualityDecisions.Blocked;
        }

        if (input.UseSeparatedCoverage)
        {
            var hardInsufficientReason = input.ExistingInsufficientReasons.Any(static reason => reason is
                "NoRelevantEvidence" or "ProductMismatch" or "VersionMismatch" or
                "TopicConflict" or "ConflictingEvidence");
            if (input.Evidence.Count == 0 || hardInsufficientReason ||
                grounding < rules.InsufficientGrounding || missingEvidenceCoverage.Count > 0)
            {
                return AnswerQualityDecisions.InsufficientEvidence;
            }

            if (missingAnswerCoverage.Count > 0)
            {
                return AnswerQualityDecisions.NeedsReview;
            }
        }
        else if (input.Evidence.Count == 0 ||
                 input.ExistingInsufficientReasons.Count > 0 ||
                 grounding < rules.InsufficientGrounding ||
                 coverage < rules.InsufficientCoverage)
        {
            return AnswerQualityDecisions.InsufficientEvidence;
        }

        if (conflictCount > 0 || leakageCount > 0)
        {
            return AnswerQualityDecisions.NeedsReview;
        }

        var ready = directness >= rules.MinimumDirectness &&
            grounding >= rules.MinimumGrounding &&
            topicAlignment >= rules.MinimumTopicAlignment &&
            coverage >= rules.MinimumCoverage &&
            technicalFidelity >= rules.MinimumTechnicalFidelity &&
            actionability >= rules.MinimumActionability &&
            customerReadiness >= rules.MinimumCustomerReadiness;
        return ready ? AnswerQualityDecisions.CustomerReady : AnswerQualityDecisions.NeedsReview;
    }

    private static double CalculateGrounding(
        AnswerQualityEvaluationInput input,
        int claimCount,
        int unsupportedCount,
        double topicAlignment)
    {
        if (input.Evidence.Count == 0)
        {
            return 0;
        }
        if (claimCount > 0)
        {
            return Clamp(1.0 - (unsupportedCount / (double)claimCount));
        }

        return topicAlignment >= 0.75 ? 1.0 : topicAlignment >= 0.5 ? 0.6 : 0.3;
    }

    private static double CalculateTopicAlignment(
        TopicEntityProfile query,
        TopicConflictAssessment comparison)
    {
        if (comparison.TopicConflict)
        {
            return 0;
        }
        if (query.Features.Count > 0 || query.Components.Count > 0)
        {
            return comparison.HasTopicMatch ? 1.0 : 0.25;
        }
        if (query.Products.Count > 0)
        {
            return comparison.MatchedProducts.Count > 0 ? 1.0 : 0.5;
        }
        return 1.0;
    }

    private static double CalculateDirectness(string answer, double topicAlignment, double coverage)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return 0;
        }
        if (InsufficientMarkers.Any(marker => answer.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return 0.25;
        }

        var score = (topicAlignment * 0.65) + (coverage * 0.35);
        return answer.Trim().Length < 20 ? score * 0.5 : Clamp(score);
    }

    private static double CalculateActionability(
        TopicEntityProfile query,
        string answer,
        IReadOnlySet<string> observed)
    {
        var needsAction = query.Intents.Contains("HowTo", StringComparer.Ordinal) ||
            query.Intents.Contains("Command", StringComparer.Ordinal) ||
            query.Intents.Contains("Configuration", StringComparer.Ordinal);
        if (!needsAction)
        {
            return 1.0;
        }

        if (query.Features.Contains("Stream", StringComparer.Ordinal))
        {
            var streamChecks = new[]
            {
                observed.Contains("Setup"),
                observed.Contains("Association"),
                observed.Contains("Verification"),
            };
            return streamChecks.Count(static value => value) / (double)streamChecks.Length;
        }

        var checks = new[]
        {
            observed.Contains("Command") || StepRegex().IsMatch(answer),
            observed.Contains("Execution") || ContainsAny(answer, "実行", "操作", "run", "execute"),
            observed.Contains("Verification"),
        };
        return checks.Count(static value => value) / (double)checks.Length;
    }

    private static double CalculateCustomerReadiness(string answer, int leakageCount)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return 0;
        }

        var score = 0.0;
        if (JapaneseRegex().IsMatch(answer)) score += 0.35;
        if (answer.Contains('。') || PoliteRegex().IsMatch(answer)) score += 0.25;
        if (answer.Trim().Length >= 60) score += 0.25;
        else if (answer.Trim().Length >= 30) score += 0.15;
        if (leakageCount == 0) score += 0.15;
        return Clamp(score);
    }

    private static int CountConflicts(
        AnswerQualityEvaluationInput input,
        TopicEntityProfile query,
        TopicEntityCatalog catalog)
    {
        var count = input.Evidence.Count(item =>
            TopicEntityAnalyzer.Compare(query, TopicEntityAnalyzer.Extract(item.Text, catalog)).TopicConflict);
        var versions = input.Evidence
            .SelectMany(item => TechnicalClaimExtractor.Extract(item.Text, catalog))
            .Where(static claim => claim.Kind == "Version")
            .Select(static claim => claim.NormalizedValue)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return count + (versions > 1 ? 1 : 0);
    }

    private static IReadOnlySet<string> RequiredCoverage(TopicEntityProfile query, string question)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        var upload = query.Operations.Contains("Upload", StringComparer.Ordinal) ||
            query.Features.Contains("Build upload", StringComparer.Ordinal);
        if (upload)
        {
            required.UnionWith(["Command", "Option", "Authentication", "Association", "Execution", "Verification"]);
            return required;
        }

        if (query.Features.Contains("Stream", StringComparer.Ordinal))
        {
            if (query.Intents.Contains("Overview", StringComparer.Ordinal)) required.Add("Overview");
            if (query.Intents.Any(intent => intent is "HowTo" or "Configuration"))
            {
                required.UnionWith(["Setup", "Association", "Verification"]);
            }
            return required;
        }

        if (query.Intents.Any(intent => intent is "HowTo" or "Configuration" or "Command"))
        {
            required.Add("Execution");
        }
        return required;
    }

    private static IReadOnlySet<string> ObservedCoverage(
        string answer,
        IReadOnlyList<AnswerTechnicalClaim> claims)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        if (claims.Any(static claim => claim.Kind == "Command")) observed.Add("Command");
        if (claims.Any(static claim => claim.Kind == "Option")) observed.Add("Option");
        if (ContainsAny(answer, "概要", "とは", "overview", "definition")) observed.Add("Overview");
        if (ContainsAny(answer, "設定", "作成", "手順", "setup", "configure", "create")) observed.Add("Setup");
        if (ContainsAny(answer, "認証", "ログイン", "token", "credential", "authentication")) observed.Add("Authentication");
        if (ContainsAny(answer, "接続", "関連付け", "紐付け", "project", "associate", "connection")) observed.Add("Association");
        if (ContainsAny(answer, "実行", "アップロード", "run", "execute", "upload")) observed.Add("Execution");
        if (ContainsAny(answer, "確認", "検証", "表示", "verify", "confirm", "portal")) observed.Add("Verification");
        return observed;
    }

    private static TopicEntityCatalog EnsureCatalog(TopicEntityCatalog catalog, string? productName)
    {
        var products = catalog.Products.Count > 0
            ? catalog.Products
            : string.IsNullOrWhiteSpace(productName)
                ? []
                : [new TopicAliasDefinition { CanonicalName = productName }];
        return catalog with
        {
            Products = products,
            Components = catalog.Components.Count > 0
                ? catalog.Components
                : [new TopicAliasDefinition { CanonicalName = "Validate", Aliases = ["Perforce Validate"] }],
            Features = catalog.Features.Count > 0
                ? catalog.Features
                :
                [
                    new TopicAliasDefinition { CanonicalName = "Stream", Aliases = ["ストリーム"] },
                    new TopicAliasDefinition { CanonicalName = "License", Aliases = ["ライセンス"] },
                    new TopicAliasDefinition { CanonicalName = "IDE Plugin", Aliases = ["IDEプラグイン", "Eclipse Plugin"] },
                    new TopicAliasDefinition { CanonicalName = "Build upload", Aliases = ["validate build", "build upload", "解析結果をアップロード"] },
                ],
            Objects = catalog.Objects.Count > 0
                ? catalog.Objects
                : [new TopicAliasDefinition { CanonicalName = "Analysis result", Aliases = ["解析結果"] }],
            Entities = catalog.Entities.Count > 0
                ? catalog.Entities
                :
                [
                    new TopicEntityAliasDefinition
                    {
                        Kind = TopicEntityKind.Command,
                        CanonicalValue = "qacli validate build",
                        Aliases = ["validate build"],
                    },
                ],
        };
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
    private static double Round(double value) => Math.Round(Clamp(value), 6, MidpointRounding.AwayFromZero);

    [GeneratedRegex(@"(?m)^\s*(?:\d+[.)、]|[-*・])")]
    private static partial Regex StepRegex();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]")]
    private static partial Regex JapaneseRegex();

    [GeneratedRegex(@"(?:です|ます|ください|いたします)[。\s]", RegexOptions.IgnoreCase)]
    private static partial Regex PoliteRegex();
}
