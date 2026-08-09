use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoverageEvidenceCandidate {
    pub candidate_id: String,
    #[serde(default)]
    pub original_rank: i32,
    #[serde(default)]
    pub source_type: String,
    #[serde(default)]
    pub document_id: Option<String>,
    #[serde(default)]
    pub file_path: Option<String>,
    #[serde(default)]
    pub section: Option<String>,
    #[serde(default)]
    pub text: String,
    #[serde(default)]
    pub content_hash: Option<String>,
    #[serde(default)]
    pub technical_tokens: Vec<String>,
    #[serde(default)]
    pub coverage: Vec<String>,
    #[serde(default)]
    pub ranking_score: f64,
    #[serde(default)]
    pub topic_score: f64,
    #[serde(default)]
    pub entity_score: f64,
    #[serde(default)]
    pub technical_token_score: f64,
    #[serde(default)]
    pub source_trust: f64,
    #[serde(default)]
    pub version_score: f64,
    #[serde(default)]
    pub conflict_penalty: f64,
    #[serde(default)]
    pub explicitly_excluded: bool,
    #[serde(default)]
    pub topic_conflict: bool,
    #[serde(default)]
    pub product_mismatch: bool,
    #[serde(default)]
    pub is_manually_selected: bool,
    #[serde(default)]
    pub estimated_chars: i32,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoverageEvidenceSelectionRequest {
    #[serde(default)]
    pub required_coverage: Vec<String>,
    #[serde(default)]
    pub corpus_coverage: Option<Vec<String>>,
    #[serde(default)]
    pub candidates: Vec<CoverageEvidenceCandidate>,
    #[serde(default = "default_base_max")]
    pub base_max_items: i32,
    #[serde(default = "default_expansion_max")]
    pub expansion_max_items: i32,
    #[serde(default = "default_character_budget")]
    pub character_budget: i32,
    #[serde(default = "default_minimum_quality")]
    pub minimum_quality_score: f64,
}

const fn default_base_max() -> i32 {
    3
}

const fn default_expansion_max() -> i32 {
    5
}

const fn default_character_budget() -> i32 {
    6000
}

const fn default_minimum_quality() -> f64 {
    0.30
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoverageEvidenceSelectionDecision {
    pub candidate_id: String,
    pub quality_score: f64,
    pub set_score: f64,
    pub added_coverage: Vec<String>,
    pub is_manual: bool,
    pub reason: String,
}

#[derive(Debug, Clone, PartialEq)]
pub struct CoverageEvidenceSelectionResult {
    pub selected: Vec<CoverageEvidenceCandidate>,
    pub decisions: Vec<CoverageEvidenceSelectionDecision>,
    pub required_coverage: Vec<String>,
    pub search_coverage: Vec<String>,
    pub selected_coverage: Vec<String>,
    pub missing_coverage: Vec<String>,
    pub statuses: Vec<String>,
    pub warnings: Vec<String>,
    pub redundant_candidates_skipped: usize,
    pub budget_limited: bool,
    pub estimated_chars: i64,
    pub selection_mode: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SelectorOutput {
    pub selected_evidence_ids: Vec<String>,
    pub required_coverage: Vec<String>,
    pub search_coverage: Vec<String>,
    pub selected_coverage: Vec<String>,
    pub missing_coverage: Vec<String>,
    pub selected_evidence_count: usize,
    pub redundant_candidates_skipped: usize,
    pub budget_limited: bool,
    pub warnings: Vec<String>,
    pub statuses: Vec<String>,
    pub decisions: Vec<CoverageEvidenceSelectionDecision>,
    pub estimated_chars: i64,
    pub selection_mode: String,
    #[serde(default)]
    pub selector_elapsed_ms: f64,
}

impl From<&CoverageEvidenceSelectionResult> for SelectorOutput {
    fn from(result: &CoverageEvidenceSelectionResult) -> Self {
        Self {
            selected_evidence_ids: result
                .selected
                .iter()
                .map(|item| item.candidate_id.clone())
                .collect(),
            required_coverage: result.required_coverage.clone(),
            search_coverage: result.search_coverage.clone(),
            selected_coverage: result.selected_coverage.clone(),
            missing_coverage: result.missing_coverage.clone(),
            selected_evidence_count: result.selected.len(),
            redundant_candidates_skipped: result.redundant_candidates_skipped,
            budget_limited: result.budget_limited,
            warnings: result.warnings.clone(),
            statuses: result.statuses.clone(),
            decisions: result.decisions.clone(),
            estimated_chars: result.estimated_chars,
            selection_mode: result.selection_mode.clone(),
            selector_elapsed_ms: 0.0,
        }
    }
}
