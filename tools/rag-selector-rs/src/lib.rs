pub mod errors;
pub mod models;
pub mod selector;

use std::io::{Read, Write};
use std::time::Instant;

use errors::CliError;
use models::{CoverageEvidenceSelectionRequest, SelectorOutput};

pub fn run_cli(reader: &mut impl Read, writer: &mut impl Write) -> Result<(), CliError> {
    let mut input = String::new();
    reader
        .read_to_string(&mut input)
        .map_err(|error| CliError::Input(format!("failed to read stdin: {error}")))?;
    let request: CoverageEvidenceSelectionRequest = serde_json::from_str(&input)
        .map_err(|error| CliError::Input(format!("invalid JSON input: {error}")))?;
    let selector_started = Instant::now();
    let result = selector::select(&request);
    let mut output = SelectorOutput::from(&result);
    output.selector_elapsed_ms = selector_started.elapsed().as_secs_f64() * 1000.0;
    serde_json::to_writer(&mut *writer, &output).map_err(|error| {
        CliError::Serialization(format!("failed to write JSON output: {error}"))
    })?;
    writeln!(writer).map_err(|error| {
        CliError::Serialization(format!("failed to finish JSON output: {error}"))
    })?;
    Ok(())
}
