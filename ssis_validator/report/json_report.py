"""JSON reports — machine-readable, every item accounted for."""
from __future__ import annotations

import json
from dataclasses import asdict, is_dataclass
from pathlib import Path

from ssis_validator.compare.gate1_sql import Gate1Result
from ssis_validator.compare.gate2_graph import Gate2Result
from ssis_validator.compare.gate3_schema import Gate3Result


def _counts_by_verdict(findings) -> dict[str, int]:
    """Insertion-ordered (deterministic across runs) — a plain `{v.value: ... for v in {set of
    verdicts}}` comprehension iterates a set of str-Enum members, whose order is affected by
    Python's per-process hash randomization and produced spurious reordering-only diffs on every
    re-run."""
    counts: dict[str, int] = {}
    for f in findings:
        counts[f.verdict.value] = counts.get(f.verdict.value, 0) + 1
    return counts


def _sql_item_to_dict(item) -> dict | None:
    if item is None:
        return None
    d = asdict(item)
    for key, value in d.items():
        if hasattr(value, "value"):  # Enum
            d[key] = value.value
    return d


def build_gate1_report(package: str, gate1: Gate1Result) -> dict:
    return {
        "package": package,
        "gate": "Gate1_SqlAndSchemaParity",
        "counts": _counts_by_verdict(gate1.findings),
        "findings": [
            {
                "objectName": f.object_name,
                "verdict": f.verdict.value,
                "detail": f.detail,
                "left": _sql_item_to_dict(f.left),
                "right": _sql_item_to_dict(f.right),
            }
            for f in gate1.findings
        ],
    }


def build_gate2_report(package: str, gate2: Gate2Result) -> dict:
    return {
        "package": package,
        "gate": "Gate2_GraphParity",
        "counts": _counts_by_verdict(gate2.findings),
        "findings": [
            {
                "scope": f.scope,
                "objectName": f.object_name,
                "verdict": f.verdict.value,
                "detail": f.detail,
            }
            for f in gate2.findings
        ],
    }


def _evidence_to_dict(evidence) -> dict | None:
    if evidence is None:
        return None
    if is_dataclass(evidence):
        return asdict(evidence)
    return evidence


def build_gate3_report(package: str, gate3: Gate3Result) -> dict:
    return {
        "package": package,
        "gate": "Gate3_ComponentSchemaParity",
        "counts": _counts_by_verdict(gate3.findings),
        "findings": [
            {
                "scope": f.scope,
                "objectName": f.object_name,
                "verdict": f.verdict.value,
                "detail": f.detail,
                "componentPath": f.component_path,
                "evidence": _evidence_to_dict(f.evidence),
            }
            for f in gate3.findings
        ],
    }


def write_json_report(package: str, gate1: Gate1Result, out_path: str | Path) -> None:
    Path(out_path).write_text(
        json.dumps(build_gate1_report(package, gate1), indent=2, default=str), encoding="utf-8"
    )


def write_gate2_json_report(package: str, gate2: Gate2Result, out_path: str | Path) -> None:
    Path(out_path).write_text(
        json.dumps(build_gate2_report(package, gate2), indent=2, default=str), encoding="utf-8"
    )


def write_gate3_json_report(package: str, gate3: Gate3Result, out_path: str | Path) -> None:
    Path(out_path).write_text(
        json.dumps(build_gate3_report(package, gate3), indent=2, default=str), encoding="utf-8"
    )
