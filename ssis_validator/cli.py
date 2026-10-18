"""ssis_validator validate --dtsx <path> --generated <dir> [--out <reports-root>]

Runs Gate 1 (SQL statement & schema parity), Gate 2 (control-flow/data-flow graph parity), and
Gate 3 (per-component input/output schema parity) for one .dtsx + generated-C#-folder pair.

All three subcommands (`validate`, `ai-context`, `final-summary`) share one reports-root
directory (default `gate_reports`, overridable via `--out`/`--reports-root`) and lay results out
as one folder per package under it:

    <reports-root>/<Package>/gates/<Package>.gate{1,2,3}.{json,md}
    <reports-root>/<Package>/ai_context/<Package>.ai-context.{json,md}
    <reports-root>/<Package>/ai_context/<Package>.ai-review.{json,md}
    <reports-root>/<Package>/gaps/<Package>.<Component>.correlation-gap.ai-context.{json,md}
    <reports-root>/<Package>/gaps/<Package>.<Component>.left-only-gap.ai-context.{json,md}
    <reports-root>/<Package>/final_review/<Package>.final-summary.{json,md}

Each report type gets its own named subfolder under `<reports-root>/<Package>/` -- never reusing
one folder for two different things, never leaving files loose at the package's own top level.
The `gaps/` files above only exist in `ai-context`'s non-combined (one-file-per-component) mode --
`--combined` mode folds correlation-gap/left-only-gap findings into `## Correlation gaps`/
`## Left-only gaps` sections inside the one `ai_context/<Package>.ai-context.{json,md}` file
instead, so nothing is written to `gaps/` in that mode. `final-summary` reads Gate 1-3 JSON from
`gates/` and the Tier 2 review from `ai_context/`, purely by knowing the same `reports-root` -- no
separate flags for each one.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from ssis_validator.codegen.csharp_construct_presence import any_construct_referenced
from ssis_validator.codegen.csharp_graph import extract as extract_right_graph
from ssis_validator.codegen.csharp_graph import find_package_cs_file
from ssis_validator.codegen.csharp_schema import extract as extract_right_schema
from ssis_validator.codegen.csharp_sql import extract as extract_right_sql
from ssis_validator.compare.gate1_sql import Verdict as Gate1Verdict
from ssis_validator.compare.gate1_sql import run_gate1
from ssis_validator.compare.gate2_graph import Gate2Result
from ssis_validator.compare.gate2_graph import Verdict as Gate2Verdict
from ssis_validator.compare.gate2_graph import run_gate2
from ssis_validator.compare.gate3_schema import Verdict as Gate3Verdict
from ssis_validator.compare.gate3_schema import run_gate3
from ssis_validator.dtsx.component_schema import extract as extract_left_schema
from ssis_validator.dtsx.control_flow import extract as extract_control_flow
from ssis_validator.dtsx.data_flow import extract_all as extract_data_flow
from ssis_validator.dtsx.no_translation_registry import NO_TRANSLATION_CONSTRUCTS
from ssis_validator.dtsx.semantic_extraction import extract_all_script_task_sources
from ssis_validator.dtsx.sql_extraction import extract as extract_left_sql
from ssis_validator.report.ai_context import (
    build_combined_package_packet,
    build_correlation_gap_packets,
    build_data_flow_packets,
    build_left_only_gap_packets,
    build_script_task_packets,
    render_combined_markdown,
    render_correlation_gap_markdown,
    render_left_only_gap_markdown,
    render_markdown,
)
from ssis_validator.report.final_summary import (
    build_final_summary,
    load_ai_review_json,
    load_gate_json,
    render_final_summary_markdown,
)
from ssis_validator.report.json_report import (
    write_gate2_json_report,
    write_gate3_json_report,
    write_json_report,
)
from ssis_validator.report.markdown_report import (
    write_gate2_markdown_report,
    write_gate3_markdown_report,
    write_markdown_report,
)


def _build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="ssis_validator")
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate = subparsers.add_parser("validate", help="Run the validation gates for one package pair.")
    validate.add_argument("--dtsx", required=True, help="Path to the .dtsx source file.")
    validate.add_argument("--generated", required=True, help="Path to the generated C# project folder.")
    validate.add_argument(
        "--out", default="gate_reports",
        help=(
            "Root reports directory (same root `ai-context` and `final-summary` use). Gate "
            "1-3 reports for this package are written under <out>/<package>/. Defaults to "
            "'gate_reports'."
        ),
    )

    ai_context = subparsers.add_parser(
        "ai-context",
        help=(
            "Emit Tier 2 (AI-assisted adjudication) evidence packets for the genuinely "
            "irreducible business-logic-equivalence findings Tier 1 cannot resolve further -- "
            "see docs/ai-validation-prompt.md. Advisory only; never merged into gate1/2/3 reports."
        ),
    )
    ai_context.add_argument("--dtsx", required=True, help="Path to the .dtsx source file.")
    ai_context.add_argument("--generated", required=True, help="Path to the generated C# project folder.")
    ai_context.add_argument(
        "--out", default="gate_reports",
        help=(
            "Root reports directory (same root `validate` and `final-summary` use). This "
            "package's Tier 2 evidence packet is written under <out>/<package>/ai_context/. "
            "Defaults to 'gate_reports', so Tier 1 and Tier 2 output for a package live "
            "side by side under one folder."
        ),
    )
    ai_context.add_argument(
        "--combined", action="store_true",
        help=(
            "Write ONE consolidated <package>.ai-context.{json,md} instead of one pair per "
            "component: every shared related-source file is stored once and referenced by path "
            "from each component, and the whole package can be adjudicated in a single AI "
            "roundtrip. Recommended when tokens/API calls are limited."
        ),
    )

    final_summary = subparsers.add_parser(
        "final-summary",
        help=(
            "Merge Gate 1-3 JSON reports with a Tier 2 ai-review.json (if present) into one "
            "Tool-Verified/Exception/AI-Verified/Human-Needs-Review summary. Purely mechanical -- "
            "no LLM call, no re-parsing of .dtsx/.cs."
        ),
    )
    final_summary.add_argument(
        "--package", required=True,
        help="Package name (matches <package>.gate1/2/3.json and <package>.ai-review.json).",
    )
    final_summary.add_argument(
        "--reports-root", default="gate_reports",
        help=(
            "Root reports directory -- the same root passed to `validate` and `ai-context` "
            "for this package. Gate 1-3 JSON is read from <reports-root>/<package>/, a Tier 2 "
            "<package>.ai-review.json (if any) from <reports-root>/<package>/ai_context/, and "
            "the final-summary is written back into <reports-root>/<package>/. Defaults to "
            "'gate_reports'."
        ),
    )

    return parser


def _resolve_generated_dir(generated_dir: str, package: str) -> str:
    """`--generated` must be one package's own generated folder (the one directly containing
    `<package>.cs`, e.g. `.../generate/Package_Advanced`) -- never the shared parent folder that
    holds every package's subfolder side by side (e.g. `.../generate`). Passing the parent by
    mistake doesn't fail loudly: `find_package_cs_file` only looks at the top level of whatever
    it's given, so it silently finds no orchestration file at all (Gate 2 gets skipped, and any
    correlation-gap packet loses its per-component excerpt); meanwhile every OTHER right-side
    extractor (SQL text, declared-schema classes, construct-presence, related-source/candidate
    search) scans its directory recursively, so it silently pulls in every other package's
    generated code too instead of just this one's -- two different-looking failures from the same
    one root cause (confirmed real case, 2026-09-14: `--generated` pointed at the shared root
    while `--dtsx` named only Package_Advanced).

    This resolves the common, confidently-detectable shape of that mistake -- `generated_dir`
    itself has no `<package>.cs` but a `<package>/<package>.cs` subfolder of it does -- and
    quietly corrects it, printing what it did so the correction is never silent. Anything else (a
    genuinely different/wrong path, or a package with no orchestration file at all) is left
    untouched for the existing `find_package_cs_file` None-handling to deal with, same as before.
    """
    root = Path(generated_dir)
    if (root / f"{package}.cs").exists():
        return generated_dir
    nested = root / package
    if (nested / f"{package}.cs").exists():
        print(
            f"  (--generated {root} has no {package}.cs of its own, but {nested} does -- using "
            f"that instead of scanning every package under {root} together)"
        )
        return str(nested)
    return generated_dir


def run_validate(dtsx_path: str, generated_dir: str, reports_root: str = "gate_reports") -> int:
    """Writes this package's Gate 1-3 reports under `<reports_root>/<package>/gates/` -- never
    directly into `<reports_root>/<package>/` itself -- so a multi-package reports root stays one
    folder per package (confirmed real confusion, 2026-09-14: harder to tell at a glance which
    files belong to which package), and within that, each report type gets its own named
    subfolder (`gates/`, `ai_context/`, `gaps/`, `final_review/`) rather than reusing one folder
    for two different things or leaving files loose at the package's own top level.
    """
    dtsx_path_obj = Path(dtsx_path)
    package = dtsx_path_obj.stem
    generated_dir = _resolve_generated_dir(generated_dir, package)
    out_dir_obj = Path(reports_root) / package / "gates"
    out_dir_obj.mkdir(parents=True, exist_ok=True)

    left_sql = extract_left_sql(dtsx_path_obj)
    right_sql = extract_right_sql(generated_dir)
    gate1 = run_gate1(left_sql.sql_items, right_sql.sql_items)
    write_json_report(package, gate1, out_dir_obj / f"{package}.gate1.json")
    write_markdown_report(package, gate1, out_dir_obj / f"{package}.gate1.md")

    print(f"Gate 1 — {package}: {len(gate1.findings)} SQL item(s) accounted for.")
    for verdict in Gate1Verdict:
        n = sum(1 for f in gate1.findings if f.verdict == verdict)
        if n:
            print(f"  {verdict.value}: {n}")
    if left_sql.disabled_executables:
        print(f"  (excluded {len(left_sql.disabled_executables)} Disabled=True executable(s) from the left-side inventory)")
    if left_sql.unrecognized_component_types:
        print(f"  Unrecognized left-side component types: {left_sql.unrecognized_component_types}")

    # Gate 3 is computed before Gate 2 is printed/written (though still reported afterwards, in
    # the usual 1-2-3 order) because Gate 2's own data-flow-internal check defers to Gate 3's
    # verdict for a component when both cover it -- Gate 3's real per-component schema comparison
    # is stronger evidence than Gate 2's own plain name-presence check (see
    # compare/gate2_graph.py's _compare_data_flow_internals docstring). None of Gate 3's own
    # inputs depend on Gate 2's, so this reordering changes nothing about what either gate itself
    # computes.
    left_schema = extract_left_schema(dtsx_path_obj)
    right_schema = extract_right_schema(generated_dir)
    all_construct_names = sorted({
        name for names in NO_TRANSLATION_CONSTRUCTS.values() for name in names
    })
    present_constructs = frozenset(
        name for name in all_construct_names
        if any_construct_referenced(generated_dir, (name,))
    )
    gate3 = run_gate3(
        left_schema, left_sql.sql_items, right_sql.sql_items, right_schema, present_constructs,
    )
    gate3_verdicts_by_name = {f.object_name: f.verdict.value for f in gate3.findings}

    cs_file = find_package_cs_file(generated_dir)
    gate2_blocking = False
    if cs_file is None:
        print("Gate 2 — skipped: could not locate the generated package's top-level orchestration .cs file.")
    else:
        control_flow = extract_control_flow(dtsx_path_obj)
        data_flow = extract_data_flow(dtsx_path_obj)
        right_graph = extract_right_graph(cs_file)
        cs_text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
        gate2 = run_gate2(control_flow, data_flow, right_graph, cs_text, gate3_verdicts_by_name)
        write_gate2_json_report(package, gate2, out_dir_obj / f"{package}.gate2.json")
        write_gate2_markdown_report(package, gate2, out_dir_obj / f"{package}.gate2.md")

        print(f"Gate 2 — {package}: {len(gate2.findings)} graph item(s) accounted for.")
        for verdict in Gate2Verdict:
            n = sum(1 for f in gate2.findings if f.verdict == verdict)
            if n:
                print(f"  {verdict.value}: {n}")
        gate2_blocking = any(
            f.verdict in (Gate2Verdict.MISMATCH, Gate2Verdict.LEFT_ONLY, Gate2Verdict.RIGHT_ONLY)
            for f in gate2.findings
        )

    write_gate3_json_report(package, gate3, out_dir_obj / f"{package}.gate3.json")
    write_gate3_markdown_report(package, gate3, out_dir_obj / f"{package}.gate3.md")

    print(f"Gate 3 — {package}: {len(gate3.findings)} component I/O shape(s) accounted for.")
    for verdict in Gate3Verdict:
        n = sum(1 for f in gate3.findings if f.verdict == verdict)
        if n:
            print(f"  {verdict.value}: {n}")
    gate3_blocking = any(
        f.verdict in (Gate3Verdict.MISMATCH, Gate3Verdict.LEFT_ONLY) for f in gate3.findings
    )

    gate1_blocking = any(
        f.verdict in (
            Gate1Verdict.MISMATCH, Gate1Verdict.LEFT_ONLY, Gate1Verdict.RIGHT_ONLY,
            Gate1Verdict.UNRECOGNIZED,
        )
        for f in gate1.findings
    )
    return 1 if (gate1_blocking or gate2_blocking or gate3_blocking) else 0


def run_ai_context(
    dtsx_path: str, generated_dir: str, reports_root: str = "gate_reports", combined: bool = False,
) -> int:
    """Writes this package's Tier 2 evidence packet under `<reports_root>/<package>/ai_context/`
    -- the same `<reports_root>/<package>/` folder `run_validate` writes Gate 1-3 into, plus one
    more `ai_context/` level -- so Tier 1 and Tier 2 output for a package always live together,
    and `run_final_summary` can find both from the single `reports_root` value. In non-combined
    mode, correlation-gap and left-only-gap packets (a different kind of question from a
    `## Components` packet -- see report/ai_context.py) go to a sibling `gaps/` folder instead of
    `ai_context/`, so the two kinds of question are never mixed by file location.
    """
    dtsx_path_obj = Path(dtsx_path)
    package = dtsx_path_obj.stem
    generated_dir = _resolve_generated_dir(generated_dir, package)
    out_dir_obj = Path(reports_root) / package / "ai_context"
    out_dir_obj.mkdir(parents=True, exist_ok=True)

    left_sql = extract_left_sql(dtsx_path_obj)
    right_sql = extract_right_sql(generated_dir)
    left_schema = extract_left_schema(dtsx_path_obj)
    right_schema = extract_right_schema(generated_dir)
    all_construct_names = sorted({
        name for names in NO_TRANSLATION_CONSTRUCTS.values() for name in names
    })
    present_constructs = frozenset(
        name for name in all_construct_names
        if any_construct_referenced(generated_dir, (name,))
    )
    gate1 = run_gate1(left_sql.sql_items, right_sql.sql_items)
    gate3 = run_gate3(
        left_schema, left_sql.sql_items, right_sql.sql_items, right_schema, present_constructs,
    )
    gate3_verdicts_by_name = {f.object_name: f.verdict.value for f in gate3.findings}

    package_cs_file = find_package_cs_file(generated_dir)
    task_sources = extract_all_script_task_sources(dtsx_path_obj)

    # Gate 2 is needed only for its own LeftOnly findings (control-flow/concurrency/failure/event
    # scopes) feeding build_left_only_gap_packets below -- same computation run_validate already
    # does, skipped the same way when no orchestration .cs file exists.
    gate2: Gate2Result | None = None
    if package_cs_file is not None:
        control_flow = extract_control_flow(dtsx_path_obj)
        data_flow = extract_data_flow(dtsx_path_obj)
        right_graph = extract_right_graph(package_cs_file)
        cs_text = package_cs_file.read_text(encoding="utf-8-sig", errors="replace")
        gate2 = run_gate2(control_flow, data_flow, right_graph, cs_text, gate3_verdicts_by_name)

    packets = build_data_flow_packets(package, dtsx_path_obj, gate3, package_cs_file, generated_dir)
    packets += build_script_task_packets(
        package, dtsx_path_obj, task_sources, package_cs_file, generated_dir,
    )
    correlation_gap_packets = build_correlation_gap_packets(
        package, dtsx_path_obj, gate1.findings, gate3, package_cs_file, generated_dir,
    )
    left_only_gap_packets = build_left_only_gap_packets(
        package, dtsx_path_obj, gate2, gate3, generated_dir,
    )

    if not packets and not correlation_gap_packets and not left_only_gap_packets:
        print(f"AI context — {package}: no genuinely irreducible findings survived Tier 1.")
        return 0

    if combined:
        merged = build_combined_package_packet(
            package, dtsx_path_obj, package_cs_file, packets, correlation_gap_packets,
            left_only_gap_packets,
        )
        json_text = json.dumps(merged, indent=2, default=str)
        md_text = render_combined_markdown(merged)
        (out_dir_obj / f"{package}.ai-context.json").write_text(json_text, encoding="utf-8")
        (out_dir_obj / f"{package}.ai-context.md").write_text(md_text, encoding="utf-8")
        print(
            f"AI context — {package}: {len(packets)} component(s) + "
            f"{len(correlation_gap_packets)} correlation gap(s) + "
            f"{len(left_only_gap_packets)} left-only gap(s) combined into one packet "
            f"({len(merged['sources'])} distinct shared source file(s), written once each) "
            f"— {len(md_text)} bytes, written to {out_dir_obj}."
        )
    else:
        for packet in packets:
            stem = f"{package}.{packet['component_name']}"
            (out_dir_obj / f"{stem}.ai-context.json").write_text(
                json.dumps(packet, indent=2, default=str), encoding="utf-8",
            )
            (out_dir_obj / f"{stem}.ai-context.md").write_text(render_markdown(packet), encoding="utf-8")

        # Correlation-gap and left-only-gap packets are structurally different questions from a
        # '## Components' packet ("which generated code even represents this, if any" / "Tier 1
        # confirmed nothing was generated" vs. "does this code implement the same logic") -- kept
        # in their own sibling `gaps/` folder, not mixed into `ai_context/`, so the two kinds of
        # question are never confused by their file location either.
        gaps_dir = Path(reports_root) / package / "gaps"
        if correlation_gap_packets or left_only_gap_packets:
            gaps_dir.mkdir(parents=True, exist_ok=True)
        for packet in correlation_gap_packets:
            stem = f"{package}.{packet['component_name']}.correlation-gap"
            (gaps_dir / f"{stem}.ai-context.json").write_text(
                json.dumps(packet, indent=2, default=str), encoding="utf-8",
            )
            (gaps_dir / f"{stem}.ai-context.md").write_text(
                render_correlation_gap_markdown(packet), encoding="utf-8",
            )
        for packet in left_only_gap_packets:
            stem = f"{package}.{packet['component_name']}.left-only-gap"
            (gaps_dir / f"{stem}.ai-context.json").write_text(
                json.dumps(packet, indent=2, default=str), encoding="utf-8",
            )
            (gaps_dir / f"{stem}.ai-context.md").write_text(
                render_left_only_gap_markdown(packet), encoding="utf-8",
            )
        print(
            f"AI context — {package}: {len(packets)} evidence packet(s) written to {out_dir_obj}, "
            f"{len(correlation_gap_packets)} correlation gap packet(s) + "
            f"{len(left_only_gap_packets)} left-only gap packet(s) written to {gaps_dir}."
        )
        for packet in packets:
            print(f"  [{packet['scope']}] {packet['component_name']}")
        for packet in correlation_gap_packets:
            print(f"  [CorrelationGap] {packet['component_name']}")
        for packet in left_only_gap_packets:
            print(f"  [{packet['scope']}] {packet['component_name']}")
    return 0


def run_final_summary(package: str, reports_root: str = "gate_reports") -> int:
    """Reads Gate 1-3 JSON from `<reports_root>/<package>/gates/` (written by `run_validate`) and
    a Tier 2 `ai-review.json` from `<reports_root>/<package>/ai_context/` (written by a Tier 2
    review following `run_ai_context`'s packet), and writes the merged summary into
    `<reports_root>/<package>/final_review/` -- one `reports_root` value covers all three, since
    `run_validate`/`run_ai_context` already agree on this same per-package layout.
    """
    package_dir = Path(reports_root) / package
    gates_dir = package_dir / "gates"
    ai_context_dir = package_dir / "ai_context"
    final_review_dir = package_dir / "final_review"
    final_review_dir.mkdir(parents=True, exist_ok=True)

    gate1 = load_gate_json(gates_dir / f"{package}.gate1.json")
    gate2 = load_gate_json(gates_dir / f"{package}.gate2.json")
    gate3 = load_gate_json(gates_dir / f"{package}.gate3.json")
    if gate1 is None or gate3 is None:
        print(
            f"Final summary — {package}: missing gate1/gate3 report(s) in {gates_dir} "
            "— run `validate` first."
        )
        return 1

    ai_review_path = ai_context_dir / f"{package}.ai-review.json"
    ai_review = load_ai_review_json(ai_review_path)

    summary = build_final_summary(package, gate1, gate2, gate3, ai_review)
    (final_review_dir / f"{package}.final-summary.json").write_text(
        json.dumps(summary, indent=2, default=str), encoding="utf-8",
    )
    (final_review_dir / f"{package}.final-summary.md").write_text(
        render_final_summary_markdown(summary), encoding="utf-8",
    )

    print(f"Final summary — {package}:")
    print(f"  Tool-Verified: {len(summary['toolVerified'])}")
    print(f"  Exception: {len(summary['exception'])}")
    print(f"  AI-Verified: {len(summary['aiVerified'])}")
    print(f"  Human-Needs-Review: {len(summary['humanNeedsReview'])}")
    if ai_review is None:
        print(
            f"  (no Tier 2 ai-review.json found at {ai_review_path} — every Unverifiable/LeftOnly "
            "finding fell to Human-Needs-Review)"
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = _build_arg_parser()
    args = parser.parse_args(argv)
    if args.command == "validate":
        return run_validate(args.dtsx, args.generated, args.out)
    if args.command == "ai-context":
        return run_ai_context(args.dtsx, args.generated, args.out, args.combined)
    if args.command == "final-summary":
        return run_final_summary(args.package, args.reports_root)
    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
