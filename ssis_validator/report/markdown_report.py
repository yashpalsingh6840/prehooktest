"""Markdown reports — human-readable, every item accounted for."""
from __future__ import annotations

from pathlib import Path

from ssis_validator.compare.gate1_sql import Gate1Result
from ssis_validator.compare.gate1_sql import Verdict as Gate1Verdict
from ssis_validator.compare.gate2_graph import Gate2Result
from ssis_validator.compare.gate2_graph import Verdict as Gate2Verdict
from ssis_validator.compare.gate3_schema import Gate3Result
from ssis_validator.compare.gate3_schema import Verdict as Gate3Verdict

_GATE1_VERDICT_ORDER = [
    Gate1Verdict.MISMATCH, Gate1Verdict.LEFT_ONLY, Gate1Verdict.RIGHT_ONLY,
    Gate1Verdict.UNRECOGNIZED, Gate1Verdict.UNVERIFIABLE, Gate1Verdict.NORMALIZED_MATCH,
    Gate1Verdict.MATCH,
]
_GATE2_VERDICT_ORDER = [
    Gate2Verdict.MISMATCH, Gate2Verdict.LEFT_ONLY, Gate2Verdict.RIGHT_ONLY,
    Gate2Verdict.UNVERIFIABLE, Gate2Verdict.MATCH,
]
_GATE3_VERDICT_ORDER = [
    Gate3Verdict.MISMATCH, Gate3Verdict.LEFT_ONLY, Gate3Verdict.UNVERIFIABLE,
    Gate3Verdict.NORMALIZED_MATCH, Gate3Verdict.MATCH,
]


def _format_gate1_finding(f) -> str:
    lines = [f"### `{f.object_name}` — **{f.verdict.value}**", "", f.detail, ""]
    if f.left is not None:
        lines.append(
            f"- Left: `{f.left.executable_path}` ({f.left.component_type}, `{f.left.source_property}`)"
        )
    if f.right is not None:
        lines.append(f"- Right: `{f.right.file_path}` ({f.right.shape})")
    lines.append("")
    return "\n".join(lines)


def build_gate1_markdown(package: str, gate1: Gate1Result) -> str:
    lines = [
        f"# Gate 1 report — {package}", "",
        "SQL statement & schema parity. Every SQL item from both sides is listed below with a "
        "verdict and evidence — never a bare pass/fail.", "",
    ]
    counts = {v: sum(1 for f in gate1.findings if f.verdict == v) for v in _GATE1_VERDICT_ORDER}
    lines += ["| Verdict | Count |", "|---|---|"]
    lines += [f"| {v.value} | {counts[v]} |" for v in _GATE1_VERDICT_ORDER if counts[v]]
    lines.append("")
    for v in _GATE1_VERDICT_ORDER:
        findings = [f for f in gate1.findings if f.verdict == v]
        if not findings:
            continue
        lines.append(f"## {v.value} ({len(findings)})")
        lines.append("")
        lines += [_format_gate1_finding(f) for f in findings]
    return "\n".join(lines)


def _format_gate2_finding(f) -> str:
    return f"### [{f.scope}] `{f.object_name}` — **{f.verdict.value}**\n\n{f.detail}\n"


def build_gate2_markdown(package: str, gate2: Gate2Result) -> str:
    lines = [
        f"# Gate 2 report — {package}", "",
        "Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both "
        "sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-"
        "internal component checks are scoped to `Unverifiable` presence-only evidence; full "
        "per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-"
        "Plan.md and docs/dtsx-graph-notes.md §6).", "",
    ]
    counts = {v: sum(1 for f in gate2.findings if f.verdict == v) for v in _GATE2_VERDICT_ORDER}
    lines += ["| Verdict | Count |", "|---|---|"]
    lines += [f"| {v.value} | {counts[v]} |" for v in _GATE2_VERDICT_ORDER if counts[v]]
    lines.append("")
    for v in _GATE2_VERDICT_ORDER:
        findings = [f for f in gate2.findings if f.verdict == v]
        if not findings:
            continue
        lines.append(f"## {v.value} ({len(findings)})")
        lines.append("")
        lines += [_format_gate2_finding(f) for f in findings]
    return "\n".join(lines)


def _format_gate3_finding(f) -> str:
    return f"### [{f.scope}] `{f.object_name}` — **{f.verdict.value}**\n\n```\n{f.detail}\n```\n"


def build_gate3_markdown(package: str, gate3: Gate3Result) -> str:
    lines = [
        f"# Gate 3 report — {package}", "",
        "Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type "
        "shape only — never actual data or execution. Every pipeline component is accounted for: "
        "a component with no populated externalMetadataColumns on either side (a pure "
        "pipeline-internal transform) is `Unverifiable`, not a silent pass.", "",
    ]
    counts = {v: sum(1 for f in gate3.findings if f.verdict == v) for v in _GATE3_VERDICT_ORDER}
    lines += ["| Verdict | Count |", "|---|---|"]
    lines += [f"| {v.value} | {counts[v]} |" for v in _GATE3_VERDICT_ORDER if counts[v]]
    lines.append("")
    for v in _GATE3_VERDICT_ORDER:
        findings = [f for f in gate3.findings if f.verdict == v]
        if not findings:
            continue
        lines.append(f"## {v.value} ({len(findings)})")
        lines.append("")
        lines += [_format_gate3_finding(f) for f in findings]
    return "\n".join(lines)


def write_markdown_report(package: str, gate1: Gate1Result, out_path: str | Path) -> None:
    Path(out_path).write_text(build_gate1_markdown(package, gate1), encoding="utf-8")


def write_gate2_markdown_report(package: str, gate2: Gate2Result, out_path: str | Path) -> None:
    Path(out_path).write_text(build_gate2_markdown(package, gate2), encoding="utf-8")


def write_gate3_markdown_report(package: str, gate3: Gate3Result, out_path: str | Path) -> None:
    Path(out_path).write_text(build_gate3_markdown(package, gate3), encoding="utf-8")
