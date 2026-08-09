# RAG Evidence Selector (Rust)

This crate is a deterministic Rust port of `CoverageAwareEvidenceSelector`.
It does not replace the C# selector. SupportCaseManager uses it only when the
`UseRustEvidenceSelector` feature flag is enabled, and falls back to C# for any
Rust process or result-validation failure.

## Scope and security

- Reads one JSON request from stdin and writes one JSON result to stdout.
- Writes diagnostics only to stderr.
- Does not use the network, filesystem, shell, temporary files, API keys, or
  customer-data persistence.
- Contains no `unsafe` Rust.
- Dependencies are limited to `serde` and `serde_json`.

## CLI contract

```text
stdin:  CoverageEvidenceSelectionRequest JSON (camelCase)
stdout: selector result JSON only
stderr: diagnostic text only
exit 0: success
exit 2: input validation error
exit 3: selector error
exit 4: output serialization error
```

The result includes `selectorElapsedMs` for algorithm time. The C# caller
measures total process time separately so startup overhead remains visible.

Run `rag-selector-rs --version` to print the executable version.

## Development

```powershell
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
cargo build --release
```

The shared parity fixture is
`../rag-lab/samples/phase18_coverage_selection_cases.json`. Do not change its
expected values to accommodate this port.

## Packaging

Run `package.ps1` after a release build. It copies only the release executable
to `artifacts/rag-selector/win-x64/tools/rag-selector/rag-selector-rs.exe`.
Place that `tools/rag-selector` directory under the WPF application directory.
The executable is deliberately not committed. When it is absent, the WPF app
continues with the existing C# selector.

For a nonstandard deployment, set `rustEvidenceSelectorExecutablePath` in the
AI assistant settings or the `RAG_SELECTOR_RS_EXE` environment variable.
Production path resolution never depends on Cargo's `target` directory.
