"""JSON reports — machine-readable, every item accounted for."""
from __future__ import annotations

import json
from dataclasses import asdict
from pathlib import Path

from ssis_validator.compare.gate1_sql import Gate1Result
from ssis_validator.compare.gate2_graph import Gate2Result


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
        "counts": {
            v.value: sum(1 for f in gate1.findings if f.verdict.value == v.value)
            for v in {f.verdict for f in gate1.findings}
        },
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
        "counts": {
            v.value: sum(1 for f in gate2.findings if f.verdict.value == v.value)
            for v in {f.verdict for f in gate2.findings}
        },
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


def write_json_report(package: str, gate1: Gate1Result, out_path: str | Path) -> None:
    Path(out_path).write_text(
        json.dumps(build_gate1_report(package, gate1), indent=2, default=str), encoding="utf-8"
    )


def write_gate2_json_report(package: str, gate2: Gate2Result, out_path: str | Path) -> None:
    Path(out_path).write_text(
        json.dumps(build_gate2_report(package, gate2), indent=2, default=str), encoding="utf-8"
    )
