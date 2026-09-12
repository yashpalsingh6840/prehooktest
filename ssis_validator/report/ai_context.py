"""Tier 2 evidence-packet builder — see docs/ai-validation-prompt.md and
SSIS-Rewrite-Validation-Plan.md's two-tier architecture (Phase C).

Packages ONLY what Tier 1 (Gate 1/2/3, already independence-audited) has resolved and genuinely
cannot judge further:

- A Gate 3 `InternalTransform` `Unverifiable` finding whose component type has a registered
  dtsx/semantic_extraction.py extractor (DerivedColumn, ConditionalSplit, Lookup, Aggregate,
  MergeJoin, Script Component) — these are business-logic-equivalence questions, not structural
  gaps (Phase A already turned every tool-solvable structural gap into a real `LeftOnly`; what
  remains here is deliberately out of Tier 1's reach).
- Every Script Task's source (a control-flow node, so it never appears in any Gate 2/3 finding at
  all — Gate 2 can only confirm its call site exists, never that its body is correct).

Independence, enforced by construction, not just instruction: every packet is assembled purely
from values Tier 1 already extracted/resolved (dtsx semantic evidence, the owning generated
method's brace-matched body text via codegen/csharp_graph.py's own extractor). This module reads
no directory listing and no file beyond the one right-side file Gate 2/codegen already resolved —
it never touches gaps/, extract/, svk/, coverage-report.md, or a generated project's own
README.md, the same boundary Tier 1 respects (see CLAUDE.md's independence rule).
"""
from __future__ import annotations

import json
from dataclasses import asdict, is_dataclass
from pathlib import Path

from ssis_validator.codegen.csharp_graph import _extract_method_bodies
from ssis_validator.codegen.csharp_related_source import collect_related_sources
from ssis_validator.compare.gate3_schema import Gate3Result
from ssis_validator.compare.gate3_schema import Verdict as Gate3Verdict
from ssis_validator.dtsx.semantic_extraction import ScriptSemantics


def _evidence_to_dict(evidence) -> dict | None:
    if evidence is None:
        return None
    if is_dataclass(evidence):
        return asdict(evidence)
    return evidence


def _dft_name_for(component_path: str) -> str | None:
    parts = component_path.split("\\")
    return parts[-2] if len(parts) >= 2 else None


def _method_name_for(container_name: str, package: str) -> str:
    """Mirrors compare/gate2_graph.py's own `_right_method_for_container`: the package root's
    generated method is always `RunAsync`; every other container (a Data Flow Task or a Script
    Task alike) is its own object name."""
    return "RunAsync" if container_name == package else container_name


def _read_own_file(package_cs_file: Path | None) -> str | None:
    if package_cs_file is None:
        return None
    return package_cs_file.read_text(encoding="utf-8-sig", errors="replace")


def build_data_flow_packets(
    package: str, dtsx_path: str | Path, gate3: Gate3Result, package_cs_file: Path | None,
    generated_dir: str | Path,
) -> list[dict]:
    own_text = _read_own_file(package_cs_file)
    method_bodies = _extract_method_bodies(own_text) if own_text else {}
    packets = []
    for finding in gate3.findings:
        if finding.verdict != Gate3Verdict.UNVERIFIABLE:
            continue
        if finding.scope != "InternalTransform" or finding.evidence is None:
            continue
        dft_name = _dft_name_for(finding.component_path)
        method_name = _method_name_for(dft_name, package) if dft_name else None
        excerpt = method_bodies.get(method_name) if method_name else None
        packets.append({
            "package": package,
            "scope": "DataFlowComponent",
            "component_name": finding.object_name,
            "component_path": finding.component_path,
            "dtsx_file": str(dtsx_path),
            "generated_file_path": str(package_cs_file) if package_cs_file else None,
            "generated_method_name": method_name,
            "generated_method_excerpt": excerpt,
            "related_generated_sources": collect_related_sources(
                generated_dir, excerpt, own_text, str(package_cs_file), finding.object_name,
            ),
            "tier1_detail": finding.detail,
            "evidence": _evidence_to_dict(finding.evidence),
        })
    return packets


def build_script_task_packets(
    package: str, dtsx_path: str | Path, task_sources: dict[str, ScriptSemantics],
    package_cs_file: Path | None, generated_dir: str | Path,
) -> list[dict]:
    own_text = _read_own_file(package_cs_file)
    method_bodies = _extract_method_bodies(own_text) if own_text else {}
    packets = []
    for task_name, semantics in task_sources.items():
        method_name = _method_name_for(task_name, package)
        excerpt = method_bodies.get(method_name)
        packets.append({
            "package": package,
            "scope": "ScriptTask",
            "component_name": task_name,
            "component_path": None,
            "dtsx_file": str(dtsx_path),
            "generated_file_path": str(package_cs_file) if package_cs_file else None,
            "generated_method_name": method_name,
            "generated_method_excerpt": excerpt,
            "related_generated_sources": collect_related_sources(
                generated_dir, excerpt, own_text, str(package_cs_file), task_name,
            ),
            "tier1_detail": (
                "Gate 2 can only confirm this Script Task's call site exists in the expected "
                "control-flow position (or is guarded/absorbed/inlined into a failure handler) "
                "— it cannot judge whether the generated method body faithfully reproduces the "
                "original script's logic."
            ),
            "evidence": _evidence_to_dict(semantics),
        })
    return packets


def render_markdown(packet: dict) -> str:
    lines = [
        f"# AI evidence packet — {packet['package']} / {packet['component_name']}", "",
        f"- Scope: `{packet['scope']}`",
        (
            f"- Component path: `{packet['component_path']}`" if packet["component_path"]
            else "- Component path: (control-flow level — not a data-flow component)"
        ),
        f"- .dtsx file: `{packet['dtsx_file']}`",
        f"- Generated file: `{packet['generated_file_path']}`",
        f"- Generated method: `{packet['generated_method_name']}`",
        "",
        "## Tier 1 finding", "",
        packet["tier1_detail"] or "(none)", "",
        "## Left-side (SSIS) semantic evidence", "",
        "```json",
        json.dumps(packet["evidence"], indent=2, default=str),
        "```", "",
        "## Right-side (generated C#) method excerpt", "",
        "```csharp",
        packet["generated_method_excerpt"] or "(not found)",
        "```", "",
    ]
    related = packet.get("related_generated_sources") or {}
    if related:
        lines += [
            "## Related generated source", "",
            "The excerpt above only wires together factory calls; each referenced identifier's "
            "own definition (where found in this package's own generated files) follows, since "
            "that is typically where the actual per-row/per-script logic lives.", "",
        ]
        for name, entries in related.items():
            for entry in entries:
                lines += [
                    f"### `{name}` — `{entry['file']}`", "",
                    "```csharp", entry["source"], "```", "",
                ]
    return "\n".join(lines)
