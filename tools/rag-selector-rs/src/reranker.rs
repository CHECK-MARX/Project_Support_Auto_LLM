use crate::models::CoverageEvidenceCandidate;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RerankBreakdown {
    pub final_score: f64,
}

/// The production V2 scorer is intentionally pure and deterministic. The C# selector
/// remains available only when the Rust worker cannot produce a response.
pub fn score(candidate: &CoverageEvidenceCandidate) -> RerankBreakdown {
    // SemanticScore is emitted only by the Hybrid V2 retrieval path.  Legacy and
    // shadow requests intentionally retain the long-standing shared C#/Rust score.
    if candidate.semantic_score == 0.0 {
        return RerankBreakdown {
            final_score: clamp(
                (0.45 * clamp(candidate.ranking_score))
                    + (0.20 * clamp(candidate.topic_score))
                    + (0.10 * clamp(candidate.entity_score))
                    + (0.10 * clamp(candidate.technical_token_score))
                    + (0.10 * clamp(candidate.source_trust))
                    + (0.05 * clamp(candidate.version_score))
                    + candidate.conflict_penalty,
            ),
        };
    }
    let lexical = clamp(candidate.lexical_score.max(candidate.ranking_score));
    let semantic = clamp(candidate.semantic_score);
    let exact = clamp(
        candidate
            .exact_match_score
            .max(candidate.technical_token_score),
    );
    let alias = clamp(candidate.alias_match_score.max(candidate.topic_score));
    let product = clamp(candidate.product_match_score);
    let feature = clamp(candidate.feature_match_score);
    let operation = clamp(candidate.operation_match_score);
    let intent = clamp(candidate.intent_match_score.max(candidate.entity_score));
    let generic_penalty = if exact == 0.0 && alias == 0.0 && feature == 0.0 && operation == 0.0 {
        -0.18
    } else {
        0.0
    };
    let final_score = clamp(
        (0.20 * lexical)
            + (0.16 * semantic)
            + (0.14 * exact)
            + (0.10 * alias)
            + (0.10 * product)
            + (0.09 * feature)
            + (0.08 * operation)
            + (0.05 * intent)
            + (0.05 * clamp(candidate.source_trust))
            + (0.03 * clamp(candidate.version_score))
            + candidate.conflict_penalty.min(0.0)
            + generic_penalty,
    );
    RerankBreakdown { final_score }
}

fn clamp(value: f64) -> f64 {
    value.clamp(0.0, 1.0)
}

#[cfg(test)]
mod tests {
    use super::score;
    use crate::models::CoverageEvidenceCandidate;

    #[test]
    fn exact_operation_candidate_beats_generic_candidate() {
        let exact = CoverageEvidenceCandidate {
            ranking_score: 0.5,
            lexical_score: 0.8,
            semantic_score: 0.8,
            exact_match_score: 1.0,
            operation_match_score: 1.0,
            product_match_score: 1.0,
            feature_match_score: 1.0,
            source_trust: 1.0,
            ..Default::default()
        };
        let generic = CoverageEvidenceCandidate {
            ranking_score: 0.7,
            lexical_score: 0.7,
            semantic_score: 0.2,
            product_match_score: 1.0,
            source_trust: 1.0,
            ..Default::default()
        };
        assert!(score(&exact).final_score > score(&generic).final_score);
    }
}
