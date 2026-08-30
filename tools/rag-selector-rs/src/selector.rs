use std::cmp::Ordering;
use std::collections::BTreeSet;

use crate::models::{
    CoverageEvidenceCandidate, CoverageEvidenceSelectionDecision, CoverageEvidenceSelectionRequest,
    CoverageEvidenceSelectionResult,
};
use crate::reranker;

const NEAR_DUPLICATE_THRESHOLD: f64 = 0.88;
const MAX_CANDIDATE_INPUTS: usize = 1_024;

#[derive(Debug)]
struct Assessment {
    candidate: CoverageEvidenceCandidate,
    new_required_coverage_count: usize,
    quality_score: f64,
    set_score: f64,
}

pub fn select(request: &CoverageEvidenceSelectionRequest) -> CoverageEvidenceSelectionResult {
    let required = normalize(&request.required_coverage);
    let candidates_truncated = request.candidates.len() > MAX_CANDIDATE_INPUTS;
    let mut candidates: Vec<_> = request
        .candidates
        .iter()
        .filter(|item| !item.candidate_id.trim().is_empty())
        .take(MAX_CANDIDATE_INPUTS)
        .cloned()
        .collect();
    candidates.sort_by(|left, right| {
        left.original_rank
            .cmp(&right.original_rank)
            .then_with(|| left.candidate_id.cmp(&right.candidate_id))
    });
    let base_max = request.base_max_items.clamp(1, 5) as usize;
    let expansion_max = request.expansion_max_items.max(base_max as i32).clamp(1, 5) as usize;
    let budget = i64::from(request.character_budget.max(0));
    let minimum_quality = clamp(request.minimum_quality_score);
    let mut selected = Vec::new();
    let mut decisions = Vec::new();
    let mut selected_ids = BTreeSet::new();
    let mut selected_coverage = BTreeSet::new();
    let mut warnings = Vec::new();
    if candidates_truncated {
        warnings.push("CandidateInputTruncated".to_owned());
    }
    let mut used_chars = 0_i64;
    let mut redundant_ids = BTreeSet::new();
    let mut budget_limited = false;

    for manual in candidates.iter().filter(|item| item.is_manually_selected) {
        if !selected_ids.insert(manual.candidate_id.clone()) {
            continue;
        }
        let manual_coverage = normalize(&manual.coverage);
        let added_coverage = manual_coverage
            .iter()
            .filter(|item| required.contains(item) && !selected_coverage.contains(*item))
            .cloned()
            .collect();
        selected_coverage.extend(manual_coverage);
        used_chars += estimated_chars(manual);
        let quality = quality_score(manual);
        selected.push(manual.clone());
        decisions.push(CoverageEvidenceSelectionDecision {
            candidate_id: manual.candidate_id.clone(),
            quality_score: quality,
            set_score: quality,
            added_coverage,
            is_manual: true,
            reason: "ManualSelection".to_owned(),
        });
    }

    if selected.len() > expansion_max {
        warnings.push("ManualSelectionExceedsLimit".to_owned());
    }
    if !selected.is_empty() && used_chars > budget {
        warnings.push("ManualSelectionExceedsCharacterBudget".to_owned());
    }

    let mut eligible: Vec<_> = candidates
        .iter()
        .filter(|item| !selected_ids.contains(&item.candidate_id))
        .filter(|item| !item.explicitly_excluded && !item.topic_conflict && !item.product_mismatch)
        .filter(|item| quality_score(item) >= minimum_quality)
        .cloned()
        .collect();

    while !eligible.is_empty() && selected.len() < expansion_max {
        let coverage_complete =
            required.is_empty() || required.iter().all(|item| selected_coverage.contains(item));
        if selected.len() >= base_max && coverage_complete {
            break;
        }

        let has_anchor = !selected.is_empty();
        let mut ranked: Vec<_> = eligible
            .iter()
            .map(|item| assess(item, &selected, &selected_coverage, &required))
            .collect();
        ranked.sort_by(|left, right| {
            let left_gain = if has_anchor {
                left.new_required_coverage_count
            } else {
                0
            };
            let right_gain = if has_anchor {
                right.new_required_coverage_count
            } else {
                0
            };
            right_gain
                .cmp(&left_gain)
                .then_with(|| {
                    let left_diverse = has_anchor
                        && selected.iter().all(|item| {
                            !item
                                .source_type
                                .eq_ignore_ascii_case(&left.candidate.source_type)
                        });
                    let right_diverse = has_anchor
                        && selected.iter().all(|item| {
                            !item
                                .source_type
                                .eq_ignore_ascii_case(&right.candidate.source_type)
                        });
                    right_diverse.cmp(&left_diverse)
                })
                .then_with(|| descending_f64(left.set_score, right.set_score))
                .then_with(|| descending_f64(left.quality_score, right.quality_score))
                .then_with(|| {
                    left.candidate
                        .original_rank
                        .cmp(&right.candidate.original_rank)
                })
                .then_with(|| {
                    left.candidate
                        .candidate_id
                        .cmp(&right.candidate.candidate_id)
                })
        });

        let mut chosen = None;
        for assessment in ranked {
            if selected.len() >= base_max && assessment.new_required_coverage_count == 0 {
                continue;
            }
            if is_redundant(
                &assessment.candidate,
                &selected,
                assessment.new_required_coverage_count > 0,
            ) {
                redundant_ids.insert(assessment.candidate.candidate_id.clone());
                continue;
            }
            let candidate_chars = estimated_chars(&assessment.candidate);
            if !selected.is_empty() && used_chars + candidate_chars > budget {
                if assessment.new_required_coverage_count > 0 {
                    budget_limited = true;
                }
                continue;
            }
            chosen = Some(assessment);
            break;
        }

        let Some(chosen) = chosen else {
            break;
        };
        let candidate_coverage = normalize(&chosen.candidate.coverage);
        let added_coverage = candidate_coverage
            .iter()
            .filter(|item| required.contains(item) && !selected_coverage.contains(*item))
            .cloned()
            .collect();
        selected_coverage.extend(candidate_coverage);
        used_chars += estimated_chars(&chosen.candidate);
        selected_ids.insert(chosen.candidate.candidate_id.clone());
        eligible.retain(|item| item.candidate_id != chosen.candidate.candidate_id);
        selected.push(chosen.candidate.clone());
        decisions.push(CoverageEvidenceSelectionDecision {
            candidate_id: chosen.candidate.candidate_id,
            quality_score: chosen.quality_score,
            set_score: chosen.set_score,
            added_coverage,
            is_manual: false,
            reason: if selected.len() == 1 {
                "QualityAnchor".to_owned()
            } else {
                "CoverageCompletion".to_owned()
            },
        });
    }

    let search_coverage = normalize(
        &candidates
            .iter()
            .flat_map(|item| item.coverage.iter().cloned())
            .collect::<Vec<_>>(),
    );
    let missing_coverage = required
        .iter()
        .filter(|item| !selected_coverage.contains(*item))
        .cloned()
        .collect::<Vec<_>>();
    let statuses = build_statuses(
        request.corpus_coverage.as_deref(),
        &required,
        &search_coverage,
        &missing_coverage,
        budget_limited,
        selected.len() >= expansion_max,
    );

    CoverageEvidenceSelectionResult {
        selected,
        decisions,
        required_coverage: required,
        search_coverage,
        selected_coverage: selected_coverage.into_iter().collect(),
        missing_coverage,
        statuses,
        warnings,
        redundant_candidates_skipped: redundant_ids.len(),
        budget_limited,
        estimated_chars: used_chars,
        selection_mode: "CoverageAware".to_owned(),
    }
}

fn assess(
    candidate: &CoverageEvidenceCandidate,
    selected: &[CoverageEvidenceCandidate],
    selected_coverage: &BTreeSet<String>,
    required: &[String],
) -> Assessment {
    let candidate_coverage = normalize(&candidate.coverage);
    let new_required_coverage_count = candidate_coverage
        .iter()
        .filter(|item| required.contains(item) && !selected_coverage.contains(*item))
        .count();
    let quality = quality_score(candidate);
    let source_diversity = if selected.is_empty()
        || selected.iter().all(|item| {
            !item
                .source_type
                .eq_ignore_ascii_case(&candidate.source_type)
        }) {
        1.0
    } else {
        0.0
    };
    let technical_diversity = if selected.is_empty() {
        1.0
    } else {
        1.0 - selected
            .iter()
            .map(|item| jaccard(&item.technical_tokens, &candidate.technical_tokens))
            .fold(0.0_f64, f64::max)
    };
    let coverage_value = if selected.is_empty() {
        0.0
    } else {
        new_required_coverage_count as f64
    };
    let set_score = coverage_value
        + (0.35 * quality)
        + (0.05 * source_diversity)
        + (0.05 * technical_diversity);
    Assessment {
        candidate: candidate.clone(),
        new_required_coverage_count,
        quality_score: quality,
        set_score,
    }
}

fn quality_score(item: &CoverageEvidenceCandidate) -> f64 {
    reranker::score(item).final_score
}

fn is_redundant(
    candidate: &CoverageEvidenceCandidate,
    selected: &[CoverageEvidenceCandidate],
    adds_required_coverage: bool,
) -> bool {
    for existing in selected {
        if candidate.content_hash.as_ref().is_some_and(|hash| {
            existing
                .content_hash
                .as_ref()
                .is_some_and(|other| hash.eq_ignore_ascii_case(other))
        }) {
            return true;
        }
        let same_document = same(&candidate.document_id, &existing.document_id)
            || same(&candidate.file_path, &existing.file_path);
        let same_section = same(&candidate.section, &existing.section);
        if same_document && (same_section || !adds_required_coverage) {
            return true;
        }
        if same_document_family(&candidate.document_title, &existing.document_title)
            && has_analysis_procedure_signature(&candidate.text)
            && has_analysis_procedure_signature(&existing.text)
        {
            return true;
        }
        if has_enough_text_for_similarity(&candidate.text, &existing.text)
            && text_similarity(&candidate.text, &existing.text) >= NEAR_DUPLICATE_THRESHOLD
        {
            return true;
        }
        // Shared commands identify the operation, not duplicate evidence. Keep separate
        // sources when their document/content differs so they can cover distinct steps.
    }
    false
}

fn build_statuses(
    corpus_coverage: Option<&[String]>,
    required: &[String],
    search_coverage: &[String],
    missing_coverage: &[String],
    budget_limited: bool,
    item_limit_reached: bool,
) -> Vec<String> {
    let mut statuses = Vec::new();
    if corpus_coverage.is_some_and(|coverage| {
        let normalized = normalize(coverage);
        required.iter().any(|item| !normalized.contains(item))
    }) {
        statuses.push("MissingCoverageInCorpus".to_owned());
    }
    if required.iter().any(|item| !search_coverage.contains(item)) {
        statuses.push("MissingCoverageInSearchResults".to_owned());
    }
    if missing_coverage.is_empty() {
        statuses.push("CoverageSatisfied".to_owned());
    } else if (budget_limited || item_limit_reached)
        && missing_coverage
            .iter()
            .any(|item| search_coverage.contains(item))
    {
        statuses.push("SelectionBudgetExceeded".to_owned());
    } else {
        statuses.push("CoverageInsufficient".to_owned());
    }
    statuses
}

fn normalize(values: &[String]) -> Vec<String> {
    values
        .iter()
        .map(|value| value.trim())
        .filter(|value| !value.is_empty())
        .map(str::to_owned)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}

fn estimated_chars(item: &CoverageEvidenceCandidate) -> i64 {
    if item.estimated_chars > 0 {
        i64::from(item.estimated_chars)
    } else {
        item.text.chars().count() as i64
    }
}

fn same(left: &Option<String>, right: &Option<String>) -> bool {
    left.as_ref().is_some_and(|value| {
        !value.trim().is_empty()
            && right.as_ref().is_some_and(|other| {
                !other.trim().is_empty() && value.trim().eq_ignore_ascii_case(other.trim())
            })
    })
}

fn text_similarity(left: &str, right: &str) -> f64 {
    jaccard(&terms(left), &terms(right))
}

fn has_enough_text_for_similarity(left: &str, right: &str) -> bool {
    left.chars().count() >= 80
        && right.chars().count() >= 80
        && terms(left).into_iter().collect::<BTreeSet<_>>().len() >= 8
        && terms(right).into_iter().collect::<BTreeSet<_>>().len() >= 8
}

fn same_document_family(left: &Option<String>, right: &Option<String>) -> bool {
    fn normalize_title(value: Option<&String>) -> String {
        value
            .map_or("", String::as_str)
            .chars()
            .filter(|character| character.is_alphanumeric())
            .flat_map(char::to_uppercase)
            .collect()
    }

    let normalized_left = normalize_title(left.as_ref());
    normalized_left.chars().count() >= 8 && normalized_left == normalize_title(right.as_ref())
}

fn has_analysis_procedure_signature(value: &str) -> bool {
    let normalized = value.to_lowercase();
    (normalized.contains("qacli analyze") || normalized.contains("qaclianalyze"))
        && (normalized.contains("]>[")
            || normalized.contains("analyze project")
            || normalized.contains("run analysis"))
}

fn terms(value: &str) -> Vec<String> {
    value
        .split([
            ' ', '\r', '\n', '\t', ',', '.', ':', ';', '/', '\\', '-', '_',
        ])
        .filter(|term| term.chars().count() > 1)
        .map(|term| term.to_uppercase())
        .take(400)
        .collect()
}

fn jaccard(left: &[String], right: &[String]) -> f64 {
    let left_set = left
        .iter()
        .filter(|item| !item.trim().is_empty())
        .map(|item| item.to_lowercase())
        .collect::<BTreeSet<_>>();
    let right_set = right
        .iter()
        .filter(|item| !item.trim().is_empty())
        .map(|item| item.to_lowercase())
        .collect::<BTreeSet<_>>();
    if left_set.is_empty() || right_set.is_empty() {
        return 0.0;
    }
    left_set.intersection(&right_set).count() as f64 / left_set.union(&right_set).count() as f64
}

fn clamp(value: f64) -> f64 {
    value.clamp(0.0, 1.0)
}

fn descending_f64(left: f64, right: f64) -> Ordering {
    right.total_cmp(&left)
}

#[cfg(test)]
mod tests {
    use super::{MAX_CANDIDATE_INPUTS, select};
    use crate::models::{CoverageEvidenceCandidate, CoverageEvidenceSelectionRequest};

    #[test]
    fn empty_candidates_report_missing_search_coverage() {
        let result = select(&request(&["A"], vec![], 3, 5, 1000));
        assert!(result.selected.is_empty());
        assert_eq!(result.missing_coverage, ["A"]);
        assert!(
            result
                .statuses
                .contains(&"MissingCoverageInSearchResults".to_owned())
        );
    }

    #[test]
    fn one_candidate_is_selected() {
        let result = select(&request(
            &["A"],
            vec![candidate("a", 1, &["A"], 0.8)],
            3,
            5,
            1000,
        ));
        assert_eq!(ids(&result), ["a"]);
    }

    #[test]
    fn base_limits_of_three_four_and_five_are_respected() {
        for limit in 3..=5 {
            let coverage = (0..limit)
                .map(|index| format!("C{index}"))
                .collect::<Vec<_>>();
            let candidates = (0..6)
                .map(|index| {
                    candidate(
                        &format!("item-{index}"),
                        index + 1,
                        &[&format!("C{index}")],
                        0.9 - (f64::from(index) * 0.05),
                    )
                })
                .collect();
            let request = CoverageEvidenceSelectionRequest {
                required_coverage: coverage,
                candidates,
                base_max_items: limit,
                expansion_max_items: limit,
                character_budget: 10_000,
                ..request(&[], vec![], 3, 5, 10_000)
            };
            assert_eq!(select(&request).selected.len(), limit as usize);
        }
    }

    #[test]
    fn topic_conflict_product_mismatch_and_manual_exclusion_are_respected() {
        let mut excluded = candidate("excluded", 1, &["A"], 0.99);
        excluded.explicitly_excluded = true;
        let mut conflict = candidate("conflict", 2, &["A"], 0.98);
        conflict.topic_conflict = true;
        let mut mismatch = candidate("mismatch", 3, &["A"], 0.97);
        mismatch.product_mismatch = true;
        let allowed = candidate("allowed", 4, &["A"], 0.7);
        assert_eq!(
            ids(&select(&request(
                &["A"],
                vec![excluded, conflict, mismatch, allowed],
                3,
                5,
                1000,
            ))),
            ["allowed"]
        );
    }

    #[test]
    fn excluded_conflicting_and_mismatched_candidates_are_skipped() {
        let mut excluded = candidate("excluded", 1, &["A"], 0.99);
        excluded.explicitly_excluded = true;
        let mut conflict = candidate("conflict", 2, &["A"], 0.98);
        conflict.topic_conflict = true;
        let mut mismatch = candidate("mismatch", 3, &["A"], 0.97);
        mismatch.product_mismatch = true;
        let allowed = candidate("allowed", 4, &["A"], 0.7);
        let result = select(&request(
            &["A"],
            vec![excluded, conflict, mismatch, allowed],
            3,
            5,
            1000,
        ));
        assert_eq!(ids(&result), ["allowed"]);
    }

    #[test]
    fn manual_selection_is_kept_above_limit_and_budget() {
        let mut candidates = Vec::new();
        for rank in 1..=6 {
            let mut item = candidate(&format!("m{rank}"), rank, &[], 0.1);
            item.is_manually_selected = true;
            item.estimated_chars = 10;
            candidates.push(item);
        }
        let result = select(&request(&["A"], candidates, 3, 5, 1));
        assert_eq!(result.selected.len(), 6);
        assert!(
            result
                .warnings
                .contains(&"ManualSelectionExceedsLimit".to_owned())
        );
        assert!(
            result
                .warnings
                .contains(&"ManualSelectionExceedsCharacterBudget".to_owned())
        );
    }

    #[test]
    fn duplicate_hash_and_document_section_are_suppressed() {
        let mut first = candidate("first", 1, &["A"], 0.9);
        first.content_hash = Some("sha".to_owned());
        first.document_id = Some("doc".to_owned());
        first.section = Some("section".to_owned());
        let mut hash_copy = candidate("hash-copy", 2, &[], 0.8);
        hash_copy.content_hash = Some("SHA".to_owned());
        let mut section_copy = candidate("section-copy", 3, &[], 0.7);
        section_copy.document_id = Some("doc".to_owned());
        section_copy.section = Some("section".to_owned());
        let result = select(&request(
            &["A"],
            vec![first, hash_copy, section_copy],
            3,
            5,
            1000,
        ));
        assert_eq!(ids(&result), ["first"]);
        assert_eq!(result.redundant_candidates_skipped, 2);
    }

    #[test]
    fn same_document_different_section_can_add_coverage() {
        let mut auth = candidate("auth", 1, &["A"], 0.9);
        auth.document_id = Some("doc".to_owned());
        auth.section = Some("auth".to_owned());
        let mut build = candidate("build", 2, &["B"], 0.8);
        build.document_id = Some("doc".to_owned());
        build.section = Some("build".to_owned());
        let result = select(&request(&["A", "B"], vec![auth, build], 2, 5, 1000));
        assert_eq!(ids(&result), ["auth", "build"]);
    }

    #[test]
    fn near_duplicate_text_is_suppressed_even_when_it_claims_new_coverage() {
        let mut primary = candidate("primary", 1, &["A"], 0.9);
        primary.text =
            "Open the QAC project and run qacli analyze. Review the analysis progress and results."
                .to_owned();
        let mut archived_copy = candidate("archived-copy", 2, &["A", "B"], 0.8);
        archived_copy.text = primary.text.clone();

        let result = select(&request(
            &["A", "B"],
            vec![primary, archived_copy],
            2,
            3,
            5000,
        ));

        assert_eq!(ids(&result), ["primary"]);
        assert_eq!(result.redundant_candidates_skipped, 1);
    }

    #[test]
    fn archived_copy_of_same_analysis_procedure_is_suppressed() {
        let mut current = candidate("current", 1, &["A"], 0.9);
        current.document_title = Some("Perforce-QAC-Manual".to_owned());
        current.text =
            "Use [PerforceQAC]>[Analysis]>[File-based Analysis]. Run qacli analyze -P project."
                .to_owned();
        let mut archive = candidate("archive", 2, &["B"], 0.85);
        archive.document_title = Some("Perforce_QAC_Manual".to_owned());
        archive.text = "Archived wording: [PerforceQAC]>[Analysis]>[File-based Analysis], then qaclianalyze-P project and review results.".to_owned();
        let mut verification = candidate("verification", 3, &["B"], 0.7);
        verification.document_title = Some("Analysis Results Guide".to_owned());
        verification.text =
            "Review the analysis results and progress in the analysis dialog.".to_owned();

        let result = select(&request(
            &["A", "B"],
            vec![current, archive, verification],
            2,
            3,
            5000,
        ));

        assert_eq!(ids(&result), ["current", "verification"]);
        assert_eq!(result.redundant_candidates_skipped, 1);
    }

    #[test]
    fn one_generic_technical_token_does_not_make_distinct_documents_duplicates() {
        let mut install = candidate("install", 1, &["A"], 0.9);
        install.technical_tokens = vec!["QAC".to_owned()];
        install.text = "Install and configure a QAC project.".to_owned();
        let mut license = candidate("license", 2, &[], 0.8);
        license.technical_tokens = vec!["QAC".to_owned()];
        license.text = "Verify the QAC license before analysis.".to_owned();

        let result = select(&request(&["A"], vec![install, license], 2, 5, 1000));

        assert_eq!(ids(&result), ["install", "license"]);
    }

    #[test]
    fn prompt_budget_reports_limit_without_forcing_coverage() {
        let mut a = candidate("a", 1, &["A"], 0.9);
        a.estimated_chars = 100;
        let mut b = candidate("b", 2, &["B"], 0.8);
        b.estimated_chars = 150;
        let result = select(&request(&["A", "B"], vec![a, b], 2, 5, 200));
        assert_eq!(ids(&result), ["a"]);
        assert!(result.budget_limited);
    }

    #[test]
    fn candidate_input_is_bounded_before_selection_loops() {
        let mut candidates = (0..MAX_CANDIDATE_INPUTS)
            .map(|index| {
                let mut item = candidate(&format!("excluded-{index}"), index as i32, &[], 0.8);
                item.explicitly_excluded = true;
                item
            })
            .collect::<Vec<_>>();
        candidates.push(candidate("outside-limit", i32::MAX, &["A"], 0.9));

        let result = select(&request(&["A"], candidates, 3, 5, 1000));

        assert!(result.selected.is_empty());
        assert!(
            result
                .warnings
                .iter()
                .any(|warning| warning == "CandidateInputTruncated")
        );
    }

    #[test]
    fn stable_tie_break_and_repeated_execution_are_deterministic() {
        let request = request(
            &["A"],
            vec![
                candidate("b", 1, &["A"], 0.8),
                candidate("a", 1, &["A"], 0.8),
            ],
            1,
            5,
            1000,
        );
        let first = select(&request);
        for _ in 0..100 {
            assert_eq!(select(&request), first);
        }
        assert_eq!(ids(&first), ["a"]);
    }

    #[test]
    fn unicode_and_japanese_coverage_labels_are_preserved() {
        let result = select(&request(
            &["認証", "接続ＮＦＫＣ"],
            vec![candidate("日本語", 1, &["認証", "接続ＮＦＫＣ"], 0.8)],
            3,
            5,
            1000,
        ));
        assert_eq!(result.selected_coverage, ["接続ＮＦＫＣ", "認証"]);
        assert_eq!(ids(&result), ["日本語"]);
    }

    fn request(
        coverage: &[&str],
        candidates: Vec<CoverageEvidenceCandidate>,
        base_max: i32,
        expansion_max: i32,
        budget: i32,
    ) -> CoverageEvidenceSelectionRequest {
        CoverageEvidenceSelectionRequest {
            required_coverage: coverage.iter().map(|value| (*value).to_owned()).collect(),
            corpus_coverage: None,
            candidates,
            base_max_items: base_max,
            expansion_max_items: expansion_max,
            character_budget: budget,
            minimum_quality_score: 0.3,
        }
    }

    fn candidate(
        id: &str,
        rank: i32,
        coverage: &[&str],
        quality: f64,
    ) -> CoverageEvidenceCandidate {
        CoverageEvidenceCandidate {
            candidate_id: id.to_owned(),
            original_rank: rank,
            source_type: "Manual".to_owned(),
            document_title: None,
            document_id: None,
            file_path: None,
            section: None,
            text: id.to_owned(),
            content_hash: None,
            technical_tokens: Vec::new(),
            coverage: coverage.iter().map(|value| (*value).to_owned()).collect(),
            ranking_score: quality,
            topic_score: quality,
            entity_score: quality,
            technical_token_score: quality,
            source_trust: quality,
            version_score: quality,
            conflict_penalty: 0.0,
            explicitly_excluded: false,
            topic_conflict: false,
            product_mismatch: false,
            is_manually_selected: false,
            estimated_chars: 0,
            ..Default::default()
        }
    }

    fn ids(result: &crate::models::CoverageEvidenceSelectionResult) -> Vec<&str> {
        result
            .selected
            .iter()
            .map(|item| item.candidate_id.as_str())
            .collect()
    }
}
