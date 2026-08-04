from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from time import perf_counter
from typing import Any, Mapping

from .chunking import ChunkingOptions, ChunkingStrategy, chunk_document
from .evaluation import aggregate_evaluations, evaluate_results
from .io import LabPaths, load_documents, load_evaluation_cases, read_json
from .normalization import NormalizationOptions
from .reporting import write_comparison_reports
from .search import SearchFilters, SearchMethod, build_search_index


@dataclass(frozen=True, slots=True)
class EvaluationRunOutput:
    report: dict[str, Any]
    files: dict[str, Path]


def _object(value: Any, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ValueError(f"{name} must be an object")
    return value


def _string_list(value: Any, name: str) -> list[str]:
    if not isinstance(value, list) or not value or not all(
        isinstance(item, str) for item in value
    ):
        raise ValueError(f"{name} must be a non-empty array of strings")
    return value


def _positive_int_list(value: Any, name: str) -> list[int]:
    if not isinstance(value, list) or not value or not all(
        isinstance(item, int) and not isinstance(item, bool) and item > 0
        for item in value
    ):
        raise ValueError(f"{name} must be a non-empty array of positive integers")
    return sorted(set(value))


def _normalization_options(config: Mapping[str, Any]) -> NormalizationOptions:
    excluded = config.get("excludeCategories", [])
    if not isinstance(excluded, list) or not all(
        isinstance(item, str) for item in excluded
    ):
        raise ValueError("normalization.excludeCategories must be an array of strings")
    return NormalizationOptions(
        unicode_nfkc=bool(config.get("unicodeNfkc", True)),
        trim_trailing_whitespace=bool(config.get("trimTrailingWhitespace", True)),
        max_consecutive_blank_lines=int(config.get("maxConsecutiveBlankLines", 1)),
        exclude_categories=frozenset(excluded),
    )


def _filters(mode: str, product: str, target_version: str | None) -> SearchFilters | None:
    if mode == "none":
        return None
    if mode == "product":
        return SearchFilters(product_name=product)
    if mode == "product_and_version":
        return SearchFilters(product_name=product, target_version=target_version)
    raise ValueError(f"unsupported filter mode: {mode}")


def run_evaluation(
    lab_root: str | Path,
    *,
    config_path: str | Path = "config.example.json",
    report_name: str | None = None,
) -> EvaluationRunOutput:
    root = Path(lab_root).resolve(strict=True)
    config_paths = LabPaths.create(root, input_roots=(".",))
    raw_config = read_json(config_paths, config_path)
    config = _object(raw_config, "configuration")

    input_roots = _string_list(config.get("inputRoots", ["samples"]), "inputRoots")
    output_root = config.get("outputRoot", "reports/generated")
    if not isinstance(output_root, str):
        raise ValueError("outputRoot must be a string")
    paths = LabPaths.create(root, input_roots=input_roots, output_root=output_root)

    documents_file = config.get("documentsFile", "samples/documents.json")
    cases_file = config.get("evaluationCasesFile", "samples/evaluation_cases.json")
    if not isinstance(documents_file, str) or not isinstance(cases_file, str):
        raise ValueError("documentsFile and evaluationCasesFile must be strings")
    documents = load_documents(paths, documents_file)
    cases = load_evaluation_cases(paths, cases_file)

    normalization = _normalization_options(
        _object(config.get("normalization", {}), "normalization")
    )
    chunking_config = _object(config.get("chunking", {}), "chunking")
    maximum = int(chunking_config.get("maxCharacters", 1200))
    overlap = int(chunking_config.get("overlapCharacters", 120))
    evaluation_config = _object(config.get("evaluation", {}), "evaluation")
    strategies = [
        ChunkingStrategy(value)
        for value in _string_list(
            evaluation_config.get("chunkStrategies", ["fixed"]),
            "evaluation.chunkStrategies",
        )
    ]
    methods = [
        SearchMethod(value)
        for value in _string_list(
            evaluation_config.get("searchMethods", ["keyword", "bm25"]),
            "evaluation.searchMethods",
        )
    ]
    filter_modes = _string_list(
        evaluation_config.get("filterModes", ["none", "product"]),
        "evaluation.filterModes",
    )
    if any(
        mode not in {"none", "product", "product_and_version"}
        for mode in filter_modes
    ):
        raise ValueError("evaluation.filterModes contains an unsupported value")
    top_ks = _positive_int_list(
        evaluation_config.get("topK", [1, 3, 5]), "evaluation.topK"
    )
    selected_report_name = report_name or config.get("reportName", "phase3-comparison")
    if not isinstance(selected_report_name, str):
        raise ValueError("reportName must be a string")

    summary: list[dict[str, Any]] = []
    details: list[dict[str, Any]] = []
    for strategy in strategies:
        chunk_started = perf_counter()
        chunks = [
            chunk
            for document in documents
            for chunk in chunk_document(
                document,
                ChunkingOptions(
                    strategy=strategy,
                    max_characters=maximum,
                    overlap_characters=overlap,
                    normalization=normalization,
                ),
            )
        ]
        chunk_time_ms = (perf_counter() - chunk_started) * 1000.0
        for method in methods:
            index_started = perf_counter()
            index = build_search_index(chunks, method)
            index_time_ms = (perf_counter() - index_started) * 1000.0
            for filter_mode in filter_modes:
                query_runs: list[dict[str, Any]] = []
                evaluations_by_k: dict[int, list[Any]] = {value: [] for value in top_ks}
                for case in cases:
                    search_started = perf_counter()
                    results = index.search(
                        case.query,
                        top_k=max(1, len(chunks)),
                        filters=_filters(
                            filter_mode, case.product, case.target_version
                        ),
                    )
                    search_time_ms = (perf_counter() - search_started) * 1000.0
                    query_metrics: dict[str, Any] = {}
                    for top_k in top_ks:
                        evaluated = evaluate_results(
                            case,
                            results,
                            top_k=top_k,
                            search_time_ms=search_time_ms,
                        )
                        evaluations_by_k[top_k].append(evaluated)
                        query_metrics[str(top_k)] = evaluated.to_dict()
                    query_runs.append(
                        {
                            "query_id": case.query_id,
                            "product": case.product,
                            "target_version": case.target_version,
                            "query": case.query,
                            "search_time_ms": search_time_ms,
                            "metrics": query_metrics,
                            "results": [
                                result.to_public_dict()
                                for result in results[: max(top_ks)]
                            ],
                        }
                    )

                for top_k, evaluations in evaluations_by_k.items():
                    aggregate = aggregate_evaluations(evaluations)
                    summary.append(
                        {
                            "chunk_strategy": strategy.value,
                            "search_method": method.value,
                            "filter_mode": filter_mode,
                            "top_k": top_k,
                            "document_count": len(documents),
                            "chunk_count": len(chunks),
                            "chunk_generation_time_ms": chunk_time_ms,
                            "index_build_time_ms": index_time_ms,
                            **aggregate,
                        }
                    )
                details.append(
                    {
                        "chunk_strategy": strategy.value,
                        "search_method": method.value,
                        "filter_mode": filter_mode,
                        "queries": query_runs,
                    }
                )

    report = {
        "schema_version": 1,
        "generated_at_utc": datetime.now(UTC).isoformat(),
        "data_classification": "synthetic",
        "report_name": selected_report_name,
        "configuration": {
            "documents_file": Path(documents_file).name,
            "evaluation_cases_file": Path(cases_file).name,
            "chunk_strategies": [item.value for item in strategies],
            "search_methods": [item.value for item in methods],
            "filter_modes": filter_modes,
            "top_k": top_ks,
        },
        "summary": summary,
        "details": details,
    }
    files = write_comparison_reports(paths, selected_report_name, report)
    return EvaluationRunOutput(report, files)
