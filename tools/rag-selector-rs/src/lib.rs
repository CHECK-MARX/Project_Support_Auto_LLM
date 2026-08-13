pub mod errors;
pub mod models;
pub mod selector;

use std::io::{BufRead, Read, Write};
use std::time::Instant;

use errors::CliError;
use models::{CoverageEvidenceSelectionRequest, SelectorOutput};
use serde_json::{Value, json};

pub const WORKER_PROTOCOL_VERSION: u64 = 1;

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

pub fn run_worker(reader: &mut impl BufRead, writer: &mut impl Write) -> Result<(), CliError> {
    let mut line = String::new();
    loop {
        line.clear();
        let bytes = reader
            .read_line(&mut line)
            .map_err(|error| CliError::Input(format!("failed to read worker stdin: {error}")))?;
        if bytes == 0 {
            return Ok(());
        }
        let trimmed = line.trim_end_matches(['\r', '\n']);
        if trimmed.trim().is_empty() {
            write_worker_error(writer, None, "InvalidRequest", "request line is empty")?;
            continue;
        }

        let envelope: Value = match serde_json::from_str(trimmed) {
            Ok(value) => value,
            Err(_) => {
                write_worker_error(writer, None, "InvalidJson", "invalid JSON request")?;
                continue;
            }
        };
        let request_id = envelope
            .get("requestId")
            .and_then(Value::as_str)
            .map(str::to_owned);
        let operation = envelope
            .get("operation")
            .and_then(Value::as_str)
            .unwrap_or("");
        let protocol_version = envelope.get("protocolVersion").and_then(Value::as_u64);

        if operation == "hello" {
            if protocol_version != Some(WORKER_PROTOCOL_VERSION) {
                write_worker_error(
                    writer,
                    request_id.as_deref(),
                    "ProtocolVersionMismatch",
                    "unsupported protocol version",
                )?;
                continue;
            }
            write_worker_json(
                writer,
                &json!({
                    "operation": "hello",
                    "protocolVersion": WORKER_PROTOCOL_VERSION,
                    "version": env!("CARGO_PKG_VERSION")
                }),
            )?;
            continue;
        }

        if operation != "select" {
            write_worker_error(
                writer,
                request_id.as_deref(),
                "UnknownOperation",
                "unknown worker operation",
            )?;
            continue;
        }
        if protocol_version != Some(WORKER_PROTOCOL_VERSION) {
            write_worker_error(
                writer,
                request_id.as_deref(),
                "ProtocolVersionMismatch",
                "unsupported protocol version",
            )?;
            continue;
        }
        let Some(request_id) = request_id else {
            write_worker_error(writer, None, "MissingRequestId", "requestId is required")?;
            continue;
        };
        let request_value = envelope.get("request").cloned().unwrap_or(Value::Null);
        let request: CoverageEvidenceSelectionRequest = match serde_json::from_value(request_value)
        {
            Ok(value) => value,
            Err(_) => {
                write_worker_error(
                    writer,
                    Some(&request_id),
                    "InvalidRequest",
                    "request payload is invalid",
                )?;
                continue;
            }
        };

        let selector_started = Instant::now();
        let result = selector::select(&request);
        let mut output = SelectorOutput::from(&result);
        output.selector_elapsed_ms = selector_started.elapsed().as_secs_f64() * 1000.0;
        write_worker_json(
            writer,
            &json!({
                "operation": "select",
                "protocolVersion": WORKER_PROTOCOL_VERSION,
                "requestId": request_id,
                "result": output
            }),
        )?;
    }
}

fn write_worker_error(
    writer: &mut impl Write,
    request_id: Option<&str>,
    code: &str,
    message: &str,
) -> Result<(), CliError> {
    write_worker_json(
        writer,
        &json!({
            "operation": "error",
            "protocolVersion": WORKER_PROTOCOL_VERSION,
            "requestId": request_id,
            "error": { "code": code, "message": message }
        }),
    )
}

fn write_worker_json(writer: &mut impl Write, value: &Value) -> Result<(), CliError> {
    serde_json::to_writer(&mut *writer, value).map_err(|error| {
        CliError::Serialization(format!("failed to write worker JSON: {error}"))
    })?;
    writeln!(writer).map_err(|error| {
        CliError::Serialization(format!("failed to finish worker JSON: {error}"))
    })?;
    writer
        .flush()
        .map_err(|error| CliError::Serialization(format!("failed to flush worker JSON: {error}")))
}

#[cfg(test)]
mod worker_tests {
    use super::{WORKER_PROTOCOL_VERSION, run_worker};
    use std::io::{BufReader, Write};

    fn run(input: &str) -> Vec<serde_json::Value> {
        let mut reader = BufReader::new(input.as_bytes());
        let mut output = Vec::new();
        run_worker(&mut reader, &mut output).expect("worker should complete");
        String::from_utf8(output)
            .expect("UTF-8 output")
            .lines()
            .map(|line| serde_json::from_str(line).expect("JSON line"))
            .collect()
    }

    fn select_line(request_id: &str, coverage: &str) -> String {
        serde_json::json!({
            "operation": "select",
            "protocolVersion": WORKER_PROTOCOL_VERSION,
            "requestId": request_id,
            "request": {
                "requiredCoverage": [coverage],
                "candidates": [{
                    "candidateId": "a",
                    "coverage": [coverage],
                    "rankingScore": 0.8,
                    "topicScore": 0.8,
                    "entityScore": 0.8,
                    "technicalTokenScore": 0.8,
                    "sourceTrust": 0.8,
                    "versionScore": 0.8
                }]
            }
        })
        .to_string()
    }

    #[test]
    fn hello_reports_protocol_and_version() {
        let lines = run("{\"operation\":\"hello\",\"protocolVersion\":1}\n");
        assert_eq!(lines[0]["operation"], "hello");
        assert_eq!(lines[0]["protocolVersion"], 1);
        assert_eq!(lines[0]["version"], env!("CARGO_PKG_VERSION"));
    }

    #[test]
    fn one_request_echoes_request_id_and_result() {
        let lines = run(&(select_line("abc", "Authentication") + "\n"));
        assert_eq!(lines[0]["requestId"], "abc");
        assert_eq!(
            lines[0]["result"]["selectedEvidenceIds"],
            serde_json::json!(["a"])
        );
    }

    #[test]
    fn multiple_requests_are_processed_in_order() {
        let input = format!("{}\n{}\n", select_line("one", "A"), select_line("two", "B"));
        let lines = run(&input);
        assert_eq!(lines.len(), 2);
        assert_eq!(lines[0]["requestId"], "one");
        assert_eq!(lines[1]["requestId"], "two");
    }

    #[test]
    fn invalid_json_does_not_prevent_next_request() {
        let input = format!("not-json\n{}\n", select_line("valid", "A"));
        let lines = run(&input);
        assert_eq!(lines[0]["error"]["code"], "InvalidJson");
        assert_eq!(lines[1]["requestId"], "valid");
    }

    #[test]
    fn invalid_request_does_not_stop_worker() {
        let input = format!(
            "{{\"operation\":\"select\",\"protocolVersion\":1,\"requestId\":\"bad\",\"request\":{{\"candidates\":\"wrong\"}}}}\n{}\n",
            select_line("good", "A")
        );
        let lines = run(&input);
        assert_eq!(lines[0]["error"]["code"], "InvalidRequest");
        assert_eq!(lines[1]["requestId"], "good");
    }

    #[test]
    fn unicode_and_japanese_are_preserved() {
        let lines = run(&(select_line("日本語-id", "認証") + "\n"));
        assert_eq!(lines[0]["requestId"], "日本語-id");
        assert_eq!(
            lines[0]["result"]["selectedCoverage"],
            serde_json::json!(["認証"])
        );
    }

    #[test]
    fn repeated_request_is_deterministic() {
        let line = select_line("same", "A");
        let lines = run(&format!("{line}\n{line}\n"));
        let mut first = lines[0]["result"].clone();
        let mut second = lines[1]["result"].clone();
        first.as_object_mut().unwrap().remove("selectorElapsedMs");
        second.as_object_mut().unwrap().remove("selectorElapsedMs");
        assert_eq!(first, second);
    }

    #[test]
    fn protocol_mismatch_returns_error_and_continues() {
        let input = format!(
            "{{\"operation\":\"hello\",\"protocolVersion\":99}}\n{}\n",
            select_line("valid", "A")
        );
        let lines = run(&input);
        assert_eq!(lines[0]["error"]["code"], "ProtocolVersionMismatch");
        assert_eq!(lines[1]["requestId"], "valid");
    }

    #[test]
    fn missing_protocol_version_returns_error_and_continues() {
        let input = format!(
            "{{\"operation\":\"hello\"}}\n{}\n",
            select_line("valid", "A")
        );
        let lines = run(&input);
        assert_eq!(lines[0]["error"]["code"], "ProtocolVersionMismatch");
        assert_eq!(lines[1]["requestId"], "valid");
    }

    #[test]
    fn eof_exits_without_output() {
        assert!(run("").is_empty());
    }

    struct FlushCountingWriter {
        bytes: Vec<u8>,
        flushes: usize,
    }

    impl Write for FlushCountingWriter {
        fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
            self.bytes.extend_from_slice(buffer);
            Ok(buffer.len())
        }

        fn flush(&mut self) -> std::io::Result<()> {
            self.flushes += 1;
            Ok(())
        }
    }

    #[test]
    fn every_response_is_flushed() {
        let input = format!("{}\n{}\n", select_line("one", "A"), select_line("two", "B"));
        let mut reader = BufReader::new(input.as_bytes());
        let mut writer = FlushCountingWriter {
            bytes: Vec::new(),
            flushes: 0,
        };
        run_worker(&mut reader, &mut writer).expect("worker should complete");
        assert_eq!(writer.flushes, 2);
    }
}
