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
- A Gate 3 `DestinationInput`/`SourceOutput` `Unverifiable` finding where Tier 1 has a real
  declared column shape but genuinely could not correlate ANY generated construct against it (see
  `build_correlation_gap_packets`) — a different, narrower question than the two above ("which
  generated code even represents this, if any" rather than "does this code implement the same
  logic"), so it gets its own `## Correlation gaps` section and a two-part judgment.
- A Gate 2 or Gate 3 `LeftOnly` finding — a STRONGER Tier 1 signal than the correlation gaps above:
  Tier 1 didn't just fail to correlate, it actively confirmed no generated counterpart exists (see
  `build_left_only_gap_packets`). Kept in its own `## Left-only gaps` section, distinct from
  `## Correlation gaps`, since the two questions differ ("Tier 1 couldn't tell" vs. "Tier 1
  actively confirmed a gap") even though both use the same bounded name-search fallback.

Independence, enforced by construction, not just instruction: every packet is assembled purely
from values Tier 1 already extracted/resolved (dtsx semantic evidence, the owning generated
method's brace-matched body text via codegen/csharp_graph.py's own extractor, or — for a
correlation gap only — a bounded, capped name-mention search across this package's own generated
files, never an open-ended one). This module reads no directory listing and no file beyond what
Gate 1/2/codegen already resolved or this bounded search returns — it never touches gaps/,
extract/, svk/, coverage-report.md, or a generated project's own README.md, the same boundary
Tier 1 respects (see CLAUDE.md's independence rule).
"""
from __future__ import annotations

import json
from dataclasses import asdict, is_dataclass
from pathlib import Path

from ssis_validator.codegen.csharp_graph import _extract_method_bodies
from ssis_validator.codegen.csharp_related_source import (
    collect_related_sources,
    extract_named_method_bodies,
    find_name_mentions,
)
from ssis_validator.compare.gate2_graph import Gate2Result
from ssis_validator.compare.gate2_graph import Verdict as Gate2Verdict
from ssis_validator.compare.gate3_schema import Gate3Result
from ssis_validator.compare.gate3_schema import Verdict as Gate3Verdict
from ssis_validator.dtsx.semantic_extraction import ScriptSemantics

# Gate 2 scopes with no data-flow angle at all -- a control-flow node/concurrency-group
# member/failure-handler/event-handler with no matching call site is genuinely new information
# a LeftOnly-gap packet can add. "DataFlow:<dft>" scope findings are excluded on purpose: those
# are Gate 2's own name-presence echo of a Gate 3 finding for the SAME component (see
# compare/gate2_graph.py's _GATE3_TO_GATE2_VERDICT) and would just duplicate the Gate-3-sourced
# packet below, not add anything.
_GATE2_LEFT_ONLY_GAP_SCOPES = ("ControlFlow", "Concurrency", "FailureHandler", "EventHandler")


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


def build_correlation_gap_packets(
    package: str, dtsx_path: str | Path, gate1_findings: list, gate3: Gate3Result,
    package_cs_file: Path | None, generated_dir: str | Path,
) -> list[dict]:
    """One packet per Gate 3 correlation-FAILURE finding: `Unverifiable`, scoped `DestinationInput`
    or `SourceOutput`, where Tier 1 has a real declared column shape (`finding.evidence`, a plain
    list Gate 3 attaches only on this specific branch -- see compare/gate3_schema.py) but could
    not correlate ANY generated construct to compare it against at all. Distinct from an
    `InternalTransform` finding (no declared shape *exists* to compare -- see
    `build_data_flow_packets`): here Tier 1 knows exactly what the SSIS side declares and simply
    couldn't find its generated counterpart.

    Two evidence sources, tried in order:
    1. Gate 1's OWN correlation, when it already matched this exact component to a generated
       SQL-bearing construct by SQL content (not name) -- confirmed real case: OLECMD_SetFlag
       (Package_Advanced) has no (schema,table) identity for Gate 3 to use, but Gate 1 already
       found and content-matched it to the `OleDbCommandStep` construction site in
       `DFT_FlagCustomers`. Reusing that is a known, established correlate, not a guess.
    2. A bounded, capped name-mention search (`codegen/csharp_related_source.find_name_mentions`)
       across this package's own generated files, when Gate 1 has nothing either (e.g.
       FlatFileDestination carries no SQL, so Gate 1 never sees it) -- a handful of short
       candidate excerpts, explicitly unconfirmed, for the AI to judge rather than a project-wide
       dump."""
    own_text = _read_own_file(package_cs_file)
    own_named_bodies = extract_named_method_bodies(own_text) if own_text else {}
    gate1_right_by_name = {f.object_name: f.right for f in gate1_findings if f.right is not None}

    packets = []
    for finding in gate3.findings:
        if finding.verdict != Gate3Verdict.UNVERIFIABLE:
            continue
        if finding.scope not in ("DestinationInput", "SourceOutput"):
            continue
        if not isinstance(finding.evidence, list):
            continue  # not this specific correlation-failure branch

        right = gate1_right_by_name.get(finding.object_name)
        known_correlate = None
        candidates = None
        if right is not None:
            excerpt = own_named_bodies.get(right.object_name)
            known_correlate = {
                "resolved_via": "Gate 1's own SQL-content correlation",
                "generated_object_name": right.object_name,
                "generated_shape": right.shape,
                "generated_file_path": right.file_path,
                "excerpt": excerpt,
                "related_generated_sources": collect_related_sources(
                    generated_dir, excerpt, own_text, str(package_cs_file), right.object_name,
                ) if excerpt else {},
            }
        else:
            candidates = find_name_mentions(generated_dir, finding.object_name)

        packets.append({
            "package": package,
            "scope": "CorrelationGap",
            "component_name": finding.object_name,
            "component_path": finding.component_path,
            "dtsx_file": str(dtsx_path),
            "tier1_detail": finding.detail,
            "declared_columns": finding.evidence,
            "known_correlate": known_correlate,
            "unconfirmed_candidates": candidates,
        })
    return packets


def build_left_only_gap_packets(
    package: str, dtsx_path: str | Path, gate2: Gate2Result | None, gate3: Gate3Result,
    generated_dir: str | Path,
) -> list[dict]:
    """One packet per `LeftOnly` finding on Gate 2 or Gate 3 -- a STRONGER Tier 1 signal than the
    `Unverifiable` correlation gaps `build_correlation_gap_packets` covers: Tier 1 didn't just fail
    to correlate, it actively confirmed no generated counterpart exists (see
    compare/gate3_schema.py's `NO_TRANSLATION_CONSTRUCTS`/source-output branches and
    compare/gate2_graph.py's call-site/concurrency-group/failure-handler/event-handler checks).

    Exists because a confirmed structural gap can still be a false negative: the generated rewrite
    may implement the same behavior under a different name or shape that Tier 1's naming-based
    correlation cannot see. Same bounded, capped name-mention search
    (`codegen/csharp_related_source.find_name_mentions`) `build_correlation_gap_packets` already
    uses for its own no-known-correlate fallback -- no new search mechanism, just a wider set of
    findings routed through it. No Gate 1 `known_correlate` reuse here: a Gate 3 `LeftOnly`
    structurally means Gate 1 had no SQL-content correlate either (Gate 1 would have let Gate 3
    resolve it otherwise), and Gate 2's control-flow scopes have no SQL angle at all."""
    packets = []
    for finding in gate3.findings:
        if finding.verdict != Gate3Verdict.LEFT_ONLY:
            continue
        packets.append({
            "package": package,
            "scope": f"Gate3:{finding.scope}",
            "component_name": finding.object_name,
            "component_path": finding.component_path or None,
            "dtsx_file": str(dtsx_path),
            "tier1_detail": finding.detail,
            "declared_evidence": _evidence_to_dict(finding.evidence),
            "unconfirmed_candidates": find_name_mentions(generated_dir, finding.object_name),
        })

    for finding in (gate2.findings if gate2 is not None else []):
        if finding.verdict != Gate2Verdict.LEFT_ONLY:
            continue
        if finding.scope not in _GATE2_LEFT_ONLY_GAP_SCOPES:
            continue
        packets.append({
            "package": package,
            "scope": f"Gate2:{finding.scope}",
            "component_name": finding.object_name,
            "component_path": None,
            "dtsx_file": str(dtsx_path),
            "tier1_detail": finding.detail,
            "declared_evidence": None,
            "unconfirmed_candidates": find_name_mentions(generated_dir, finding.object_name),
        })
    return packets


def build_combined_package_packet(
    package: str, dtsx_path: str | Path, package_cs_file: Path | None, packets: list[dict],
    correlation_gap_packets: list[dict] | None = None,
    left_only_gap_packets: list[dict] | None = None,
) -> dict:
    """Consolidates every per-component packet for one package into a single structure: every
    distinct related-source file is stored exactly ONCE (keyed by its file path) and each
    component references it by that path instead of re-embedding the same text. Concretely fixes
    a real, measured duplication: 4 components sharing one ConditionalSplitStep in
    Package_Transforms each embedded identical full copies of the same 3 sibling files. Designed
    for a single AI conversation to adjudicate every component in the package in one pass, instead
    of one roundtrip per component.

    `correlation_gap_packets` (build_correlation_gap_packets' output) fold into the SAME shared
    `sources` pool -- a known correlate's excerpt/related sources are deduped exactly like a
    regular component's."""
    sources: dict[str, str] = {}
    components = []
    for p in packets:
        related = p.get("related_generated_sources") or {}
        file_paths: list[str] = []
        for entries in related.values():
            for entry in entries:
                sources.setdefault(entry["file"], entry["source"])
                file_paths.append(entry["file"])
        components.append({
            "component_name": p["component_name"],
            "scope": p["scope"],
            "component_path": p["component_path"],
            "generated_method_name": p["generated_method_name"],
            "generated_method_excerpt": p["generated_method_excerpt"],
            "related_source_files": sorted(set(file_paths)),
            "tier1_detail": p["tier1_detail"],
            "evidence": p["evidence"],
        })

    correlation_gaps = []
    for p in correlation_gap_packets or []:
        known = p.get("known_correlate")
        known_out = None
        if known is not None:
            related = known.get("related_generated_sources") or {}
            file_paths = []
            for entries in related.values():
                for entry in entries:
                    sources.setdefault(entry["file"], entry["source"])
                    file_paths.append(entry["file"])
            known_out = {
                "resolved_via": known["resolved_via"],
                "generated_object_name": known["generated_object_name"],
                "generated_shape": known["generated_shape"],
                "generated_file_path": known["generated_file_path"],
                "excerpt": known["excerpt"],
                "related_source_files": sorted(set(file_paths)),
            }
        correlation_gaps.append({
            "component_name": p["component_name"],
            "component_path": p["component_path"],
            "tier1_detail": p["tier1_detail"],
            "declared_columns": p["declared_columns"],
            "known_correlate": known_out,
            "unconfirmed_candidates": p.get("unconfirmed_candidates"),
        })

    left_only_gaps = []
    for p in left_only_gap_packets or []:
        left_only_gaps.append({
            "scope": p["scope"],
            "component_name": p["component_name"],
            "component_path": p["component_path"],
            "tier1_detail": p["tier1_detail"],
            "declared_evidence": p["declared_evidence"],
            "unconfirmed_candidates": p.get("unconfirmed_candidates"),
        })

    return {
        "package": package,
        "dtsx_file": str(dtsx_path),
        "generated_file_path": str(package_cs_file) if package_cs_file else None,
        "sources": sources,
        "components": components,
        "correlation_gaps": correlation_gaps,
        "left_only_gaps": left_only_gaps,
    }


def render_combined_markdown(combined: dict) -> str:
    lines = [
        f"# AI evidence packet (combined) — {combined['package']}", "",
        f"- .dtsx file: `{combined['dtsx_file']}`",
        f"- Generated file: `{combined['generated_file_path']}`",
        f"- {len(combined['components'])} component(s), "
        f"{len(combined['sources'])} distinct shared source file(s) (each shown once below)",
        "",
        "Adjudicate every component below in ONE pass — see docs/ai-validation-prompt.md for the "
        "required output (one Component/Verdict/Rationale block per component). Each component "
        "references shared source files by path instead of repeating their content.",
        "",
        "## Sources", "",
    ]
    for path, text in combined["sources"].items():
        lines += [f"### `{path}`", "", "```csharp", text, "```", ""]
    lines.append("## Components")
    lines.append("")
    for c in combined["components"]:
        lines += [
            f"### {c['component_name']} (`{c['scope']}`)", "",
            (
                f"- Component path: `{c['component_path']}`" if c["component_path"]
                else "- Component path: (control-flow level)"
            ),
            f"- Generated method: `{c['generated_method_name']}`",
            "- Related source files: " + (
                ", ".join(f"`{f}`" for f in c["related_source_files"]) or "(none — see Sources above)"
            ),
            "",
            "**Tier 1 finding:** " + (c["tier1_detail"] or "(none)"),
            "",
            "**Left-side evidence:**", "",
            "```json", json.dumps(c["evidence"], separators=(",", ":"), default=str), "```", "",
            "**Right-side excerpt:**", "",
            "```csharp", c["generated_method_excerpt"] or "(not found)", "```", "",
        ]

    gaps = combined.get("correlation_gaps") or []
    if gaps:
        lines += [
            "## Correlation gaps", "",
            "Tier 1 knows what the SSIS side declares (`declared_columns`) for each of these but "
            "could not correlate ANY generated construct to compare it against. Your job here has "
            "two parts: (1) decide whether `known_correlate` (if present) or one of "
            "`unconfirmed_candidates` (if not) is genuinely the generated code for this component "
            "— cite why; (2) only if you're confident which one it is, judge whether it correctly "
            "implements `declared_columns`. If nothing plausible is present, that is "
            "`AiInconclusive`, stated as such — do not force a guess.", "",
        ]
        for g in gaps:
            lines += [
                f"### {g['component_name']}", "",
                (
                    f"- Component path: `{g['component_path']}`" if g["component_path"]
                    else "- Component path: (none)"
                ),
                "",
                "**Tier 1 finding:** " + (g["tier1_detail"] or "(none)"),
                "",
                "**Declared columns (left/SSIS side):**", "",
                "```json", json.dumps(g["declared_columns"], separators=(",", ":"), default=str), "```", "",
            ]
            known = g.get("known_correlate")
            if known is not None:
                lines += [
                    f"**Known correlate** (resolved via {known['resolved_via']}):", "",
                    f"- Generated object: `{known['generated_object_name']}` "
                    f"({known['generated_shape']}) in `{known['generated_file_path']}`",
                    "- Related source files: " + (
                        ", ".join(f"`{f}`" for f in known["related_source_files"])
                        or "(none — see Sources above)"
                    ),
                    "",
                    "```csharp", known["excerpt"] or "(not found)", "```", "",
                ]
            else:
                candidates = g.get("unconfirmed_candidates") or []
                lines.append(
                    f"**Unconfirmed candidates** (found by a bounded name search, "
                    f"{len(candidates)} match(es) — none of these are confirmed correlates):"
                )
                lines.append("")
                if not candidates:
                    lines += ["(no mentions of this component's name found anywhere in the generated project)", ""]
                for cand in candidates:
                    lines += [f"- `{cand['file']}`:", "", "```csharp", cand["excerpt"], "```", ""]

    left_only_gaps = combined.get("left_only_gaps") or []
    if left_only_gaps:
        lines += [
            "## Left-only gaps (Tier 1 confirmed no counterpart)", "",
            "Unlike a Correlation gap above, Tier 1 didn't just fail to correlate here — it "
            "actively confirmed no generated counterpart exists at all (a project-wide construct-"
            "presence check for `Gate3:InternalTransform`, a failed schema correlation for "
            "`Gate3:SourceOutput`, or a missing call site/concurrency-group member/handler for "
            "`Gate2:*`). Your job: decide whether any `unconfirmed_candidates` entry is genuinely "
            "an unmapped implementation of this item under a different name/shape — the rewrite "
            "may be functionally equivalent but not a 1:1 structural match. If so, judge it "
            "against `declared_evidence` (when present) and use `AiFoundUnmappedImplementation`. "
            "If nothing plausible is present, or a candidate clearly does not implement the "
            "missing behavior, that is `AiConfirmedGap` — Tier 1's gap stands.", "",
        ]
        for g in left_only_gaps:
            lines += [
                f"### {g['component_name']} (`{g['scope']}`)", "",
                (
                    f"- Component path: `{g['component_path']}`" if g["component_path"]
                    else "- Component path: (none — control-flow level)"
                ),
                "",
                "**Tier 1 finding:** " + (g["tier1_detail"] or "(none)"),
                "",
            ]
            if g.get("declared_evidence") is not None:
                lines += [
                    "**Declared evidence (left/SSIS side):**", "",
                    "```json", json.dumps(g["declared_evidence"], separators=(",", ":"), default=str), "```", "",
                ]
            candidates = g.get("unconfirmed_candidates") or []
            lines.append(
                f"**Unconfirmed candidates** (found by a bounded name search, "
                f"{len(candidates)} match(es) — none of these are confirmed correlates):"
            )
            lines.append("")
            if not candidates:
                lines += ["(no mentions of this component's name found anywhere in the generated project)", ""]
            for cand in candidates:
                lines += [f"- `{cand['file']}`:", "", "```csharp", cand["excerpt"], "```", ""]
    return "\n".join(lines)


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


def render_correlation_gap_markdown(packet: dict) -> str:
    lines = [
        f"# AI evidence packet (correlation gap) — {packet['package']} / {packet['component_name']}", "",
        (
            f"- Component path: `{packet['component_path']}`" if packet["component_path"]
            else "- Component path: (none)"
        ),
        f"- .dtsx file: `{packet['dtsx_file']}`",
        "",
        "Tier 1 knows what the SSIS side declares (below) but could not correlate ANY generated "
        "construct to compare it against. Your job has two parts: (1) decide whether "
        "`known_correlate` (if present) or one of the unconfirmed candidates (if not) is "
        "genuinely the generated code for this component — cite why; (2) only if you're "
        "confident which one it is, judge whether it correctly implements the declared columns "
        "below. If nothing plausible is present, that is `AiInconclusive`, stated as such — do "
        "not force a guess.", "",
        "## Tier 1 finding", "",
        packet["tier1_detail"] or "(none)", "",
        "## Declared columns (left/SSIS side)", "",
        "```json", json.dumps(packet["declared_columns"], indent=2, default=str), "```", "",
    ]
    known = packet.get("known_correlate")
    if known is not None:
        lines += [
            f"## Known correlate (resolved via {known['resolved_via']})", "",
            f"- Generated object: `{known['generated_object_name']}` ({known['generated_shape']}) "
            f"in `{known['generated_file_path']}`", "",
            "```csharp", known["excerpt"] or "(not found)", "```", "",
        ]
        related = known.get("related_generated_sources") or {}
        for name, entries in related.items():
            for entry in entries:
                lines += [
                    f"### `{name}` — `{entry['file']}`", "",
                    "```csharp", entry["source"], "```", "",
                ]
    else:
        candidates = packet.get("unconfirmed_candidates") or []
        lines.append(
            f"## Unconfirmed candidates (bounded name search, {len(candidates)} match(es) — "
            "none of these are confirmed correlates)"
        )
        lines.append("")
        if not candidates:
            lines += ["(no mentions of this component's name found anywhere in the generated project)", ""]
        for cand in candidates:
            lines += [f"### `{cand['file']}`", "", "```csharp", cand["excerpt"], "```", ""]
    return "\n".join(lines)


def render_left_only_gap_markdown(packet: dict) -> str:
    lines = [
        f"# AI evidence packet (left-only gap) — {packet['package']} / {packet['component_name']}", "",
        f"- Scope: `{packet['scope']}`",
        (
            f"- Component path: `{packet['component_path']}`" if packet["component_path"]
            else "- Component path: (none — control-flow level)"
        ),
        f"- .dtsx file: `{packet['dtsx_file']}`",
        "",
        "Tier 1 actively confirmed no generated counterpart exists for this item (a stronger "
        "signal than a correlation gap's 'could not correlate'). Your job: decide whether any "
        "`unconfirmed_candidates` entry below is genuinely an unmapped implementation under a "
        "different name/shape — the rewrite may be functionally equivalent but not a 1:1 "
        "structural match. If so, judge it against the declared evidence (when present) and use "
        "`AiFoundUnmappedImplementation`. If nothing plausible is present, or a candidate clearly "
        "does not implement the missing behavior, that is `AiConfirmedGap` — Tier 1's gap stands.", "",
        "## Tier 1 finding", "",
        packet["tier1_detail"] or "(none)", "",
    ]
    if packet.get("declared_evidence") is not None:
        lines += [
            "## Declared evidence (left/SSIS side)", "",
            "```json", json.dumps(packet["declared_evidence"], indent=2, default=str), "```", "",
        ]
    candidates = packet.get("unconfirmed_candidates") or []
    lines.append(
        f"## Unconfirmed candidates (bounded name search, {len(candidates)} match(es) — "
        "none of these are confirmed correlates)"
    )
    lines.append("")
    if not candidates:
        lines += ["(no mentions of this component's name found anywhere in the generated project)", ""]
    for cand in candidates:
        lines += [f"### `{cand['file']}`", "", "```csharp", cand["excerpt"], "```", ""]
    return "\n".join(lines)
