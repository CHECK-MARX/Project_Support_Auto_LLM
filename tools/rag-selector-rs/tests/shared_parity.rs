use std::fs;
use std::path::PathBuf;

use rag_selector_rs::models::{CoverageEvidenceCandidate, CoverageEvidenceSelectionRequest};
use rag_selector_rs::selector::select;
use serde::Deserialize;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct FixtureCase {
    id: String,
    required_coverage: Vec<String>,
    #[serde(default = "default_base_max")]
    base_max_items: i32,
    #[serde(default = "default_expansion_max")]
    expansion_max_items: i32,
    #[serde(default = "default_budget")]
    character_budget: i32,
    #[serde(default = "default_minimum_quality")]
    minimum_quality_score: f64,
    expected_selected_ids: Vec<String>,
    candidates: Vec<FixtureCandidate>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct FixtureCandidate {
    id: String,
    #[serde(default)]
    rank: i32,
    #[serde(default)]
    coverage: Vec<String>,
    #[serde(default)]
    quality: f64,
    #[serde(default)]
    source_type: Option<String>,
    #[serde(default)]
    document_id: Option<String>,
    #[serde(default)]
    file_path: Option<String>,
    #[serde(default)]
    section: Option<String>,
    #[serde(default)]
    text: Option<String>,
    #[serde(default)]
    content_hash: Option<String>,
    #[serde(default)]
    technical_tokens: Vec<String>,
    #[serde(default)]
    explicitly_excluded: bool,
    #[serde(default)]
    topic_conflict: bool,
    #[serde(default)]
    product_mismatch: bool,
    #[serde(default)]
    manually_selected: bool,
    #[serde(default)]
    estimated_chars: i32,
}

#[test]
fn shared_fixture_matches_expected_and_is_deterministic() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../rag-lab/samples/phase18_coverage_selection_cases.json");
    let cases: Vec<FixtureCase> =
        serde_json::from_str(&fs::read_to_string(path).expect("read shared fixture"))
            .expect("parse shared fixture");
    assert_eq!(cases.len(), 12);

    for fixture in cases {
        let request = request(&fixture);
        let first = select(&request);
        let second = select(&request);
        let selected_ids = first
            .selected
            .iter()
            .map(|item| item.candidate_id.clone())
            .collect::<Vec<_>>();
        assert_eq!(
            selected_ids, fixture.expected_selected_ids,
            "{}",
            fixture.id
        );
        assert_eq!(first, second, "{} was not deterministic", fixture.id);
        assert_eq!(
            first.selected.len(),
            first.decisions.len(),
            "{}",
            fixture.id
        );
        assert_eq!(
            first.selected_coverage,
            selected_coverage(&first.selected),
            "{}",
            fixture.id
        );
        let expected_missing = fixture
            .required_coverage
            .iter()
            .filter(|coverage| !first.selected_coverage.contains(coverage))
            .cloned()
            .collect::<Vec<_>>();
        assert_eq!(first.missing_coverage, expected_missing, "{}", fixture.id);
    }
}

fn request(fixture: &FixtureCase) -> CoverageEvidenceSelectionRequest {
    CoverageEvidenceSelectionRequest {
        required_coverage: fixture.required_coverage.clone(),
        corpus_coverage: None,
        candidates: fixture
            .candidates
            .iter()
            .enumerate()
            .map(|(index, item)| CoverageEvidenceCandidate {
                candidate_id: item.id.clone(),
                original_rank: if item.rank == 0 {
                    index as i32 + 1
                } else {
                    item.rank
                },
                source_type: item
                    .source_type
                    .clone()
                    .unwrap_or_else(|| "Manual".to_owned()),
                document_title: None,
                document_id: item.document_id.clone(),
                file_path: item.file_path.clone(),
                section: item.section.clone(),
                text: item.text.clone().unwrap_or_else(|| item.id.clone()),
                content_hash: item.content_hash.clone(),
                technical_tokens: item.technical_tokens.clone(),
                coverage: item.coverage.clone(),
                ranking_score: item.quality,
                topic_score: item.quality,
                entity_score: item.quality,
                technical_token_score: item.quality,
                source_trust: item.quality,
                version_score: item.quality,
                conflict_penalty: 0.0,
                explicitly_excluded: item.explicitly_excluded,
                topic_conflict: item.topic_conflict,
                product_mismatch: item.product_mismatch,
                is_manually_selected: item.manually_selected,
                estimated_chars: item.estimated_chars,
                ..Default::default()
            })
            .collect(),
        base_max_items: fixture.base_max_items,
        expansion_max_items: fixture.expansion_max_items,
        character_budget: fixture.character_budget,
        minimum_quality_score: fixture.minimum_quality_score,
    }
}

fn selected_coverage(candidates: &[CoverageEvidenceCandidate]) -> Vec<String> {
    let mut coverage = candidates
        .iter()
        .flat_map(|item| item.coverage.iter().cloned())
        .filter(|item| !item.trim().is_empty())
        .map(|item| item.trim().to_owned())
        .collect::<Vec<_>>();
    coverage.sort();
    coverage.dedup();
    coverage
}

const fn default_base_max() -> i32 {
    3
}

const fn default_expansion_max() -> i32 {
    5
}

const fn default_budget() -> i32 {
    2000
}

const fn default_minimum_quality() -> f64 {
    0.30
}
