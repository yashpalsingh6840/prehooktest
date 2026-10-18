"""Final Tool/AI/Human-review summary — a deterministic merge of already-produced JSON, never a
new AI-authored pass (per the project's "deterministic wherever judgment isn't required" ethos).

Reads what's already on disk from two prior, independent steps:
- Gate 1/2/3 JSON reports (`report/json_report.py`, written by `cli.py`'s `validate` command).
- The Tier 2 AI review's structured sidecar `<package>.ai-review.json` (a flat list of
  `{"component", "scope", "verdict", "rationale"}`, written by `/ssis-tier2-review` or the
  `ssis-tier2-adjudicate` skill per docs/ai-validation-prompt.md) -- optional; a package never
  sent to Tier 2 still gets a valid summary, it just has an empty AI-Verified bucket.

Joins every Tier 1 finding (across all three gates) to a Tier 2 verdict by `objectName` when one
exists, and buckets into four top-level groups so a human only ever needs to open
Human-Needs-Review:

- Tool-Verified: Tier 1 verdict is Match/NormalizedMatch -- resolved without any AI involvement.
- Exception: Tier 1 itself already confirmed there is nothing to review here -- not merely an
  unresolved gap, but a structurally-verified by-design exclusion (currently: an SSIS executable
  with `DTS:Disabled="True"`, which SSIS itself never runs, so a rewrite correctly generates
  nothing for it -- see `gate2_graph.py`'s `Finding.scope` docstring for the exact scope string
  this keys off). Distinct from Human-Needs-Review precisely because Tier 1's own per-item detail
  already reaches a conclusion; routing it to Human-Needs-Review anyway (as an earlier version of
  this rollup did) contradicted that per-item conclusion for no reason -- see
  `Docs/Gate-Report-Findings-2026-09-13.md` root cause 3 / suggestion 4.
- AI-Verified: Tier 1 was Unverifiable/LeftOnly and Tier 2 resolved it positively
  (AiConfirmedMatch or AiFoundUnmappedImplementation).
- Human-Needs-Review: everything else -- a real Tier 1 problem (Mismatch/RightOnly/Unrecognized),
  an Unverifiable/LeftOnly finding with no Tier 2 review yet, or a Tier 2 verdict that isn't a
  clean positive (AiSuspectedDiscrepancy/AiInconclusive/AiConfirmedGap/AiNotApplicable).
"""
from __future__ import annotations

import json
from pathlib import Path

_POSITIVE_TIER1_VERDICTS = ("Match", "NormalizedMatch")
_TIER1_PROBLEM_VERDICTS = ("Mismatch", "RightOnly", "Unrecognized")
_TIER1_GAP_VERDICTS = ("Unverifiable", "LeftOnly")
_POSITIVE_TIER2_VERDICTS = ("AiConfirmedMatch", "AiFoundUnmappedImplementation")
# Scopes where Tier 1 doesn't just fail to resolve a finding -- it structurally confirms there's
# nothing to review at all. Add a scope here only when it carries that same guarantee (the
# `Finding.scope` docstring at its point of origin must say so); never repurpose this for a
# plain "unresolved" gap, or Human-Needs-Review loses its meaning as "needs a human."
_BY_DESIGN_EXCEPTION_SCOPES = ("ControlFlow:Disabled",)


def load_gate_json(path: str | Path) -> dict | None:
    p = Path(path)
    if not p.exists():
        return None
    return json.loads(p.read_text(encoding="utf-8"))


def load_ai_review_json(path: str | Path) -> list[dict] | None:
    p = Path(path)
    if not p.exists():
        return None
    return json.loads(p.read_text(encoding="utf-8"))


def _bucket_one(gate_label: str, finding: dict, ai_by_name: dict[str, dict]) -> tuple[str, dict]:
    name = finding["objectName"]
    tier1_verdict = finding["verdict"]
    # Only look up a Tier 2 verdict for a finding Tier 1 itself left unresolved -- a Match on one
    # gate can share an objectName with an unrelated Unverifiable/LeftOnly finding on another gate
    # (e.g. a Gate 1 SQL item vs. a same-named Gate 3 schema component), and attaching that other
    # finding's AI review here would misrepresent it as reviewing THIS finding.
    ai_entry = ai_by_name.get(name) if tier1_verdict not in _POSITIVE_TIER1_VERDICTS else None
    record = {
        "gate": gate_label,
        "scope": finding.get("scope"),
        "objectName": name,
        "componentPath": finding.get("componentPath"),
        "tier1Verdict": tier1_verdict,
        "tier2Verdict": ai_entry["verdict"] if ai_entry else None,
        "tier2Rationale": ai_entry.get("rationale") if ai_entry else None,
    }

    if tier1_verdict in _POSITIVE_TIER1_VERDICTS:
        record["reason"] = "Tier 1 deterministic match — no AI involvement needed."
        return "toolVerified", record

    if finding.get("scope") in _BY_DESIGN_EXCEPTION_SCOPES:
        record["reason"] = f"Tier 1 already confirmed this by design, not a gap — {finding['detail']}"
        return "exception", record

    if tier1_verdict in _TIER1_GAP_VERDICTS and ai_entry and ai_entry["verdict"] in _POSITIVE_TIER2_VERDICTS:
        record["reason"] = f"Tier 1 {tier1_verdict}, Tier 2 resolved it as {ai_entry['verdict']}."
        return "aiVerified", record

    if tier1_verdict in _TIER1_PROBLEM_VERDICTS:
        record["reason"] = f"Tier 1 flagged {tier1_verdict} — a real discrepancy, not just an unresolved gap."
    elif tier1_verdict in _TIER1_GAP_VERDICTS and ai_entry is None:
        record["reason"] = f"Tier 1 {tier1_verdict}, no Tier 2 review has been run yet."
    elif ai_entry is not None:
        record["reason"] = f"Tier 2 returned {ai_entry['verdict']} — needs human judgment, not a clean resolution."
    else:
        record["reason"] = f"Tier 1 {tier1_verdict}."
    return "humanNeedsReview", record


def build_final_summary(
    package: str, gate1: dict, gate2: dict | None, gate3: dict, ai_review: list[dict] | None,
) -> dict:
    ai_by_name: dict[str, dict] = {}
    for entry in ai_review or []:
        ai_by_name.setdefault(entry["component"], entry)

    buckets: dict[str, list[dict]] = {
        "toolVerified": [], "exception": [], "aiVerified": [], "humanNeedsReview": [],
    }
    for gate_label, gate_report in (("Gate1", gate1), ("Gate2", gate2), ("Gate3", gate3)):
        if gate_report is None:
            continue
        for finding in gate_report["findings"]:
            bucket, record = _bucket_one(gate_label, finding, ai_by_name)
            buckets[bucket].append(record)

    return {
        "package": package,
        "toolVerified": buckets["toolVerified"],
        "exception": buckets["exception"],
        "aiVerified": buckets["aiVerified"],
        "humanNeedsReview": buckets["humanNeedsReview"],
    }


def render_final_summary_markdown(summary: dict) -> str:
    lines = [f"# Final validation summary — {summary['package']}", ""]

    def section(title: str, records: list[dict]) -> None:
        lines.append(f"## {title} ({len(records)})")
        lines.append("")
        if not records:
            lines.extend(["(none)", ""])
            return
        for r in records:
            scope = f" `{r['scope']}`" if r.get("scope") else ""
            tier2 = f" — Tier2: {r['tier2Verdict']}" if r.get("tier2Verdict") else ""
            lines.append(
                f"- [{r['gate']}]{scope} **{r['objectName']}** — Tier1: {r['tier1Verdict']}"
                f"{tier2} — {r['reason']}"
            )
        lines.append("")

    section("Tool-Verified", summary["toolVerified"])
    section("Exception", summary["exception"])
    section("AI-Verified", summary["aiVerified"])
    section("Human-Needs-Review", summary["humanNeedsReview"])
    return "\n".join(lines)
