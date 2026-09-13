"""ssis_validator validate --dtsx <path> --generated <dir> --out <dir>

Runs Gate 1 (SQL statement & schema parity), Gate 2 (control-flow/data-flow graph parity), and
Gate 3 (per-component input/output schema parity) for one .dtsx + generated-C#-folder pair.
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
    build_data_flow_packets,
    build_script_task_packets,
    render_combined_markdown,
    render_markdown,
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
    validate.add_argument("--out", required=True, help="Output directory for reports.")

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
    ai_context.add_argument("--out", required=True, help="Output directory for evidence packets.")
    ai_context.add_argument(
        "--combined", action="store_true",
        help=(
            "Write ONE consolidated <package>.ai-context.{json,md} instead of one pair per "
            "component: every shared related-source file is stored once and referenced by path "
            "from each component, and the whole package can be adjudicated in a single AI "
            "roundtrip. Recommended when tokens/API calls are limited."
        ),
    )
    return parser


def run_validate(dtsx_path: str, generated_dir: str, out_dir: str) -> int:
    dtsx_path_obj = Path(dtsx_path)
    package = dtsx_path_obj.stem
    out_dir_obj = Path(out_dir)
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


def run_ai_context(dtsx_path: str, generated_dir: str, out_dir: str, combined: bool = False) -> int:
    dtsx_path_obj = Path(dtsx_path)
    package = dtsx_path_obj.stem
    out_dir_obj = Path(out_dir)
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
    gate3 = run_gate3(
        left_schema, left_sql.sql_items, right_sql.sql_items, right_schema, present_constructs,
    )

    package_cs_file = find_package_cs_file(generated_dir)
    task_sources = extract_all_script_task_sources(dtsx_path_obj)

    packets = build_data_flow_packets(package, dtsx_path_obj, gate3, package_cs_file, generated_dir)
    packets += build_script_task_packets(
        package, dtsx_path_obj, task_sources, package_cs_file, generated_dir,
    )

    if not packets:
        print(f"AI context — {package}: no genuinely irreducible findings survived Tier 1.")
        return 0

    if combined:
        merged = build_combined_package_packet(package, dtsx_path_obj, package_cs_file, packets)
        json_text = json.dumps(merged, indent=2, default=str)
        md_text = render_combined_markdown(merged)
        (out_dir_obj / f"{package}.ai-context.json").write_text(json_text, encoding="utf-8")
        (out_dir_obj / f"{package}.ai-context.md").write_text(md_text, encoding="utf-8")
        print(
            f"AI context — {package}: {len(packets)} component(s) combined into one packet "
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
        print(f"AI context — {package}: {len(packets)} evidence packet(s) written to {out_dir_obj}.")
        for packet in packets:
            print(f"  [{packet['scope']}] {packet['component_name']}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = _build_arg_parser()
    args = parser.parse_args(argv)
    if args.command == "validate":
        return run_validate(args.dtsx, args.generated, args.out)
    if args.command == "ai-context":
        return run_ai_context(args.dtsx, args.generated, args.out, args.combined)
    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
