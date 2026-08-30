from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
import hashlib
from pathlib import Path
import re
from time import perf_counter
from typing import Any, Mapping

from .chunking import ChunkingOptions, ChunkingStrategy, chunk_document
from .embedding import EmbeddingProvider
from .evidence import EvidenceSelectionOptions, build_codex_evidence, write_codex_evidence
from .evaluation import aggregate_evaluations, evaluate_results
from .io import LabPaths, load_documents, load_evaluation_cases, read_json, write_json_report
from .models import Document, EvaluationCase
from .normalization import NormalizationOptions
from .quality import QualityGateThresholds, apply_quality_gate
from .question_relevance import (
    build_topic_entity_ranking_request,
    comparison_report,
    select_question_aware_evidence,
)
from .reporting import write_comparison_reports
from .reranking import RerankerMethod, apply_reranker
from .search import SearchFilters, SearchMethod, build_search_index
from .topic_entity_ranking import (
    build_topic_entity_comparison_section,
    rank_topic_entity_candidates,
)
from .topic_evaluation import (
    TopicMetadata,
    aggregate_topic_evaluations,
    evaluate_topic_results,
    load_topic_metadata,
)


@dataclass(frozen=True, slots=True)
class EvaluationRunOutput:
    report: dict[str, Any]
    files: dict[str, Path]


@dataclass(frozen=True, slots=True)
class EvidenceRunOutput:
    payload: dict[str, object]
    file: Path


@dataclass(frozen=True, slots=True)
class QuestionRankingComparisonOutput:
    report: dict[str, object]
    file: Path


@dataclass(frozen=True, slots=True)
class LoadedLabConfiguration:
    root: Path
    paths: LabPaths
    documents: list[Document]
    cases: list[EvaluationCase]
    normalization: NormalizationOptions
    max_characters: int
    overlap_characters: int
    strategies: list[ChunkingStrategy]
    methods: list[SearchMethod]
    rerankers: list[RerankerMethod]
    filter_modes: list[str]
    top_ks: list[int]
    case_partition: str
    quality_thresholds: QualityGateThresholds
    preferred_top_k: int
    report_name: str
    documents_file_name: str
    cases_file_name: str
    input_fingerprints: list[dict[str, Any]]
    topic_metadata: TopicMetadata | None
    topic_metadata_file_name: str | None


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


def _phase22_partition(case: EvaluationCase) -> str:
    """Use the manifest's deterministic p22-01..30 / p22-31..50 split."""
    match = re.fullmatch(r"p22-(\d{2})", case.query_id)
    if match is None:
        return "all"
    return "development" if int(match.group(1)) <= 30 else "holdout"


def _select_cases(cases: list[EvaluationCase], partition: str) -> list[EvaluationCase]:
    if partition == "all":
        return cases
    selected = [case for case in cases if _phase22_partition(case) == partition]
    if not selected:
        raise ValueError(f"evaluation.casePartition={partition} selected no cases")
    return selected


def _expected_evidence_locators(
    case: EvaluationCase, documents_by_id: Mapping[str, Document]
) -> list[dict[str, str | None]]:
    """Expose stable fixture locators without relying solely on temporary ids."""
    locators: list[dict[str, str | None]] = []
    for document_id in case.expected_document_ids:
        document = documents_by_id.get(document_id)
        if document is None:
            continue
        normalized = document.text.replace("\r\n", "\n").replace("\r", "\n")
        locators.append(
            {
                "document_id": document_id,
                "product": document.metadata.product_name,
                "document_title": document.metadata.document_name,
                "page": None,
                "section": None,
                "content_hash": hashlib.sha256(normalized.encode("utf-8")).hexdigest(),
                "source_type": document.metadata.source_type,
            }
        )
    return locators


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


def _fingerprint(path: Path) -> dict[str, Any]:
    content = path.read_bytes()
    canonical_content = content.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return {
        "file_name": path.name,
        "size_bytes": len(canonical_content),
        "sha256": hashlib.sha256(canonical_content).hexdigest(),
    }


def load_lab_configuration(
    lab_root: str | Path,
    *,
    config_path: str | Path = "config.example.json",
    report_name: str | None = None,
) -> LoadedLabConfiguration:
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
    documents_path = paths.resolve_input(documents_file)
    cases_path = paths.resolve_input(cases_file)
    evaluation_config = _object(config.get("evaluation", {}), "evaluation")
    topic_metadata_file = evaluation_config.get("topicMetadataFile")
    if topic_metadata_file is not None and not isinstance(topic_metadata_file, str):
        raise ValueError("evaluation.topicMetadataFile must be a string")
    topic_metadata_path = (
        paths.resolve_input(topic_metadata_file) if topic_metadata_file else None
    )
    topic_metadata = (
        load_topic_metadata(topic_metadata_path) if topic_metadata_path else None
    )
    normalization = _normalization_options(
        _object(config.get("normalization", {}), "normalization")
    )
    chunking_config = _object(config.get("chunking", {}), "chunking")
    maximum = int(chunking_config.get("maxCharacters", 1200))
    overlap = int(chunking_config.get("overlapCharacters", 120))
    case_partition = str(evaluation_config.get("casePartition", "all"))
    if case_partition not in {"all", "development", "holdout"}:
        raise ValueError("evaluation.casePartition must be all, development, or holdout")
    cases = _select_cases(cases, case_partition)
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
    rerankers = [
        RerankerMethod(value)
        for value in _string_list(
            evaluation_config.get("rerankers", ["none"]),
            "evaluation.rerankers",
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
    quality_config = _object(config.get("qualityGate", {}), "qualityGate")
    quality_thresholds = QualityGateThresholds.from_mapping(quality_config)
    default_preferred_top_k = 3 if 3 in top_ks else top_ks[0]
    preferred_top_k = int(
        quality_config.get("preferredTopK", default_preferred_top_k)
    )
    if preferred_top_k not in top_ks:
        raise ValueError("qualityGate.preferredTopK must be included in evaluation.topK")
    selected_report_name = report_name or config.get("reportName", "rag-comparison")
    if not isinstance(selected_report_name, str):
        raise ValueError("reportName must be a string")
    return LoadedLabConfiguration(
        root=root,
        paths=paths,
        documents=documents,
        cases=cases,
        normalization=normalization,
        max_characters=maximum,
        overlap_characters=overlap,
        strategies=strategies,
        methods=methods,
        rerankers=rerankers,
        filter_modes=filter_modes,
        top_ks=top_ks,
        case_partition=case_partition,
        quality_thresholds=quality_thresholds,
        preferred_top_k=preferred_top_k,
        report_name=selected_report_name,
        documents_file_name=Path(documents_file).name,
        cases_file_name=Path(cases_file).name,
        input_fingerprints=[
            _fingerprint(documents_path),
            _fingerprint(cases_path),
        ] + ([_fingerprint(topic_metadata_path)] if topic_metadata_path else []),
        topic_metadata=topic_metadata,
        topic_metadata_file_name=(
            topic_metadata_path.name if topic_metadata_path else None
        ),
    )


def run_evaluation(
    lab_root: str | Path,
    *,
    config_path: str | Path = "config.example.json",
    report_name: str | None = None,
    embedding_provider: EmbeddingProvider | None = None,
) -> EvaluationRunOutput:
    loaded = load_lab_configuration(
        lab_root, config_path=config_path, report_name=report_name
    )
    documents = loaded.documents
    cases = loaded.cases
    normalization = loaded.normalization
    maximum = loaded.max_characters
    overlap = loaded.overlap_characters
    strategies = loaded.strategies
    methods = loaded.methods
    rerankers = loaded.rerankers
    filter_modes = loaded.filter_modes
    top_ks = loaded.top_ks
    selected_report_name = loaded.report_name
    documents_by_id = {document.document_id: document for document in documents}

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
            base_index = build_search_index(
                chunks, method, embedding_provider=embedding_provider
            )
            index_time_ms = (perf_counter() - index_started) * 1000.0
            for reranker in rerankers:
                index = apply_reranker(base_index, reranker)
                for filter_mode in filter_modes:
                    query_runs: list[dict[str, Any]] = []
                    evaluations_by_k: dict[int, list[Any]] = {
                        value: [] for value in top_ks
                    }
                    topic_evaluations: list[dict[str, float | int]] = []
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
                        topic_metrics = (
                            evaluate_topic_results(case, results, loaded.topic_metadata)
                            if loaded.topic_metadata is not None
                            else None
                        )
                        if topic_metrics is not None:
                            topic_evaluations.append(topic_metrics)
                        query_runs.append(
                            {
                                "query_id": case.query_id,
                                "product": case.product,
                                "target_version": case.target_version,
                                "query": case.query,
                                "expected_evidence_locators": _expected_evidence_locators(
                                    case, documents_by_id
                                ),
                                "search_time_ms": search_time_ms,
                                "metrics": query_metrics,
                                "topic_metrics": topic_metrics,
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
                                "reranker": reranker.value,
                                "filter_mode": filter_mode,
                                "top_k": top_k,
                                "document_count": len(documents),
                                "chunk_count": len(chunks),
                                "chunk_generation_time_ms": chunk_time_ms,
                                "index_build_time_ms": index_time_ms,
                                **aggregate,
                                **aggregate_topic_evaluations(topic_evaluations),
                            }
                        )
                    details.append(
                        {
                            "chunk_strategy": strategy.value,
                            "search_method": method.value,
                            "reranker": reranker.value,
                            "filter_mode": filter_mode,
                            "queries": query_runs,
                        }
                    )

    summary, quality_gate = apply_quality_gate(
        summary,
        loaded.quality_thresholds,
        preferred_top_k=loaded.preferred_top_k,
    )
    report = {
        "schema_version": 4,
        "generated_at_utc": datetime.now(UTC).isoformat(),
        "data_classification": "synthetic",
        "report_name": selected_report_name,
        "configuration": {
            "documents_file": loaded.documents_file_name,
            "evaluation_cases_file": loaded.cases_file_name,
            "chunk_strategies": [item.value for item in strategies],
            "search_methods": [item.value for item in methods],
            "rerankers": [item.value for item in rerankers],
            "filter_modes": filter_modes,
            "top_k": top_ks,
            "case_partition": loaded.case_partition,
            "case_count": len(cases),
            "topic_metadata_file": loaded.topic_metadata_file_name,
            "embedding_provider": (
                embedding_provider.provider_id
                if embedding_provider is not None
                else "offline-token-hash-v1"
            ),
            "embedding_model": (
                getattr(embedding_provider, "model", None)
                if embedding_provider is not None
                else None
            ),
        },
        "input_fingerprints": loaded.input_fingerprints,
        "quality_gate": quality_gate,
        "summary": summary,
        "details": details,
    }
    files = write_comparison_reports(loaded.paths, selected_report_name, report)
    return EvaluationRunOutput(report, files)


_SAFE_OUTPUT_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$")


def run_evidence(
    lab_root: str | Path,
    *,
    query_id: str,
    config_path: str | Path = "config.example.json",
    chunk_strategy: ChunkingStrategy | str = ChunkingStrategy.HEADING,
    search_method: SearchMethod | str = SearchMethod.HYBRID,
    reranker: RerankerMethod | str = RerankerMethod.LEXICAL,
    filter_mode: str = "product_and_version",
    top_k: int = 3,
    output_name: str | None = None,
    question_aware: bool = False,
) -> EvidenceRunOutput:
    loaded = load_lab_configuration(lab_root, config_path=config_path)
    case = next((item for item in loaded.cases if item.query_id == query_id), None)
    if case is None:
        raise ValueError(f"evaluation query_id was not found: {query_id}")
    strategy = ChunkingStrategy(chunk_strategy)
    chunks = [
        chunk
        for document in loaded.documents
        for chunk in chunk_document(
            document,
            ChunkingOptions(
                strategy=strategy,
                max_characters=loaded.max_characters,
                overlap_characters=loaded.overlap_characters,
                normalization=loaded.normalization,
            ),
        )
    ]
    index = apply_reranker(
        build_search_index(chunks, SearchMethod(search_method)),
        RerankerMethod(reranker),
    )
    candidate_count = max(top_k, 30) if question_aware else top_k
    results = index.search(
        case.query,
        top_k=candidate_count,
        filters=_filters(filter_mode, case.product, case.target_version),
    )
    payload = build_codex_evidence(
        case.query,
        results,
        product=case.product,
        target_version=case.target_version,
        options=EvidenceSelectionOptions(top_k=top_k),
        question_aware=question_aware,
    )
    safe_query_id = re.sub(r"[^A-Za-z0-9_.-]", "_", query_id)[:60] or "query"
    selected_name = output_name or f"codex-evidence-{safe_query_id}"
    if not _SAFE_OUTPUT_NAME.fullmatch(selected_name):
        raise ValueError("evidence output name contains unsupported characters")
    output_file = write_codex_evidence(
        loaded.paths, f"{selected_name}.json", payload
    )
    return EvidenceRunOutput(payload=payload, file=output_file)


def run_question_ranking_comparison(
    lab_root: str | Path,
    *,
    query_id: str,
    config_path: str | Path,
    top_k: int = 3,
    output_name: str = "phase15-question-ranking-ab",
) -> QuestionRankingComparisonOutput:
    loaded = load_lab_configuration(lab_root, config_path=config_path)
    case = next((item for item in loaded.cases if item.query_id == query_id), None)
    if case is None:
        raise ValueError(f"evaluation query_id was not found: {query_id}")
    if not 1 <= top_k <= 5:
        raise ValueError("comparison top_k must be between 1 and 5")
    if not _SAFE_OUTPUT_NAME.fullmatch(output_name):
        raise ValueError("comparison output name contains unsupported characters")
    chunks = [
        chunk
        for document in loaded.documents
        for chunk in chunk_document(
            document,
            ChunkingOptions(
                strategy=ChunkingStrategy.STRUCTURED,
                max_characters=loaded.max_characters,
                overlap_characters=loaded.overlap_characters,
                normalization=loaded.normalization,
            ),
        )
    ]
    index = apply_reranker(
        build_search_index(chunks, SearchMethod.HYBRID),
        RerankerMethod.LEXICAL,
    )
    results = index.search(
        case.query,
        top_k=max(30, top_k),
        filters=_filters("product", case.product, case.target_version),
    )
    selection = select_question_aware_evidence(
        case.query, results, product=case.product, top_k=top_k
    )
    report = comparison_report(case.query, results, selection, top_k=top_k)
    report["queryId"] = case.query_id
    report["topK"] = top_k
    file = write_json_report(loaded.paths, f"{output_name}.json", report)
    return QuestionRankingComparisonOutput(report=report, file=file)


def run_topic_ranking_comparison(
    lab_root: str | Path,
    *,
    query_id: str,
    config_path: str | Path,
    top_k: int = 3,
    output_name: str = "phase16-topic-ranking-abc",
) -> QuestionRankingComparisonOutput:
    """Compare Phase 14, 15 and 16 with redacted synthetic diagnostics."""
    loaded = load_lab_configuration(lab_root, config_path=config_path)
    case = next((item for item in loaded.cases if item.query_id == query_id), None)
    if case is None:
        raise ValueError(f"evaluation query_id was not found: {query_id}")
    if not 1 <= top_k <= 5:
        raise ValueError("comparison top_k must be between 1 and 5")
    if not _SAFE_OUTPUT_NAME.fullmatch(output_name):
        raise ValueError("comparison output name contains unsupported characters")

    chunks = [
        chunk
        for document in loaded.documents
        for chunk in chunk_document(
            document,
            ChunkingOptions(
                strategy=ChunkingStrategy.STRUCTURED,
                max_characters=loaded.max_characters,
                overlap_characters=loaded.overlap_characters,
                normalization=loaded.normalization,
            ),
        )
    ]
    index = apply_reranker(
        build_search_index(chunks, SearchMethod.HYBRID),
        RerankerMethod.LEXICAL,
    )
    retrieval_started = perf_counter()
    results = index.search(
        case.query,
        top_k=max(30, top_k),
        filters=_filters("product", case.product, case.target_version),
    )
    retrieval_ms = (perf_counter() - retrieval_started) * 1000.0
    phase15 = select_question_aware_evidence(
        case.query, results, product=case.product, top_k=top_k
    )
    phase16_request = build_topic_entity_ranking_request(
        case.query,
        results,
        product=case.product,
        requested_version=case.target_version,
        top_k=top_k,
    )
    phase16_started = perf_counter()
    phase16_result = rank_topic_entity_candidates(phase16_request)
    phase16_ms = (perf_counter() - phase16_started) * 1000.0

    report = comparison_report(case.query, results, phase15, top_k=top_k)
    phase16_section, stream_condition = build_topic_entity_comparison_section(
        phase16_request,
        phase16_result,
        elapsed_ms=phase16_ms,
    )
    report["queryId"] = case.query_id
    report["topK"] = top_k
    report["phase16"] = phase16_section
    report["qualityConditions"] = [stream_condition]
    report["qualityGate"] = {
        "status": "passed" if stream_condition["passed"] is True else "failed",
        "violations": []
        if stream_condition["passed"] is True
        else [stream_condition["name"]],
    }
    report["timings"] = {
        "retrievalSearchTimeMs": round(retrieval_ms, 3),
        "phase15RankingTimeMs": round(phase15.elapsed_ms, 3),
        "phase16RankingTimeMs": round(phase16_ms, 3),
    }
    file = write_json_report(loaded.paths, f"{output_name}.json", report)
    return QuestionRankingComparisonOutput(report=report, file=file)
