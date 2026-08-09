use std::io;
use std::process::ExitCode;

fn main() -> ExitCode {
    if std::env::args().nth(1).as_deref() == Some("--version") {
        println!("rag-selector-rs {}", env!("CARGO_PKG_VERSION"));
        return ExitCode::SUCCESS;
    }

    match rag_selector_rs::run_cli(&mut io::stdin().lock(), &mut io::stdout().lock()) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::from(error.exit_code())
        }
    }
}

#[cfg(test)]
mod tests {
    use rag_selector_rs::run_cli;

    #[test]
    fn valid_json_returns_selector_output() {
        let mut input = br#"{"requiredCoverage":[],"candidates":[]}"#.as_slice();
        let mut output = Vec::new();
        run_cli(&mut input, &mut output).expect("valid request should succeed");
        let value: serde_json::Value = serde_json::from_slice(&output).expect("valid output");
        assert_eq!(value["selectedEvidenceIds"], serde_json::json!([]));
        assert_eq!(value["selectedEvidenceCount"], 0);
        assert!(value["selectorElapsedMs"].as_f64().is_some());
    }

    #[test]
    fn invalid_json_returns_input_error_without_stdout() {
        let mut input = b"not-json".as_slice();
        let mut output = Vec::new();
        let error = run_cli(&mut input, &mut output).expect_err("invalid input must fail");
        assert_eq!(error.exit_code(), 2);
        assert!(output.is_empty());
    }

    #[test]
    fn malformed_field_returns_input_error() {
        let mut input = br#"{"requiredCoverage":[],"candidates":"wrong"}"#.as_slice();
        let mut output = Vec::new();
        let error = run_cli(&mut input, &mut output).expect_err("wrong field type must fail");
        assert_eq!(error.exit_code(), 2);
    }

    #[test]
    fn missing_optional_fields_use_defaults() {
        let mut input = br#"{"candidates":[{"candidateId":"a","rankingScore":0.8,"topicScore":0.8,"entityScore":0.8,"technicalTokenScore":0.8,"sourceTrust":0.8,"versionScore":0.8}]}"#.as_slice();
        let mut output = Vec::new();
        run_cli(&mut input, &mut output).expect("optional fields should default");
        let value: serde_json::Value = serde_json::from_slice(&output).expect("valid output");
        assert_eq!(value["selectedEvidenceIds"], serde_json::json!(["a"]));
    }
}
