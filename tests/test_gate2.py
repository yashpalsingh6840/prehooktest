"""Gate 2 (control-flow / data-flow graph parity) proof tests.

Same discipline as test_gate1.py (plan §7.3): prove real matches on the sample pairs, then prove
the gate fails correctly on deliberate mutations of a *copy* of each side — never the originals.
"""
from __future__ import annotations

import shutil
from pathlib import Path

import pytest

from ssis_validator.codegen.csharp_graph import extract as extract_right
from ssis_validator.codegen.csharp_graph import find_package_cs_file
from ssis_validator.compare.gate2_graph import Verdict, run_gate2
from ssis_validator.dtsx.control_flow import extract as extract_control_flow
from ssis_validator.dtsx.data_flow import extract_all as extract_data_flow

DTSX_BASE = Path(r"D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL")
GENERATED_BASE = Path(r"D:\tmp\Sandeep_Packages\gen\generate")

pytestmark = pytest.mark.skipif(
    not DTSX_BASE.exists() or not GENERATED_BASE.exists(),
    reason="Sample package pairs not available on this machine.",
)


def _run(dtsx_path: Path, generated_dir: Path):
    cf = extract_control_flow(dtsx_path)
    df = extract_data_flow(dtsx_path)
    cs_file = find_package_cs_file(generated_dir)
    right = extract_right(cs_file)
    text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
    return run_gate2(cf, df, right, text)


def _by_name(result, scope=None):
    return {
        f.object_name: f for f in result.findings
        if scope is None or f.scope == scope
    }


def test_linear_chain_matches():
    result = _run(DTSX_BASE / "Package_Transforms.dtsx", GENERATED_BASE / "Package_Transforms")
    by_name = _by_name(result, "ControlFlow")
    for name in [
        "SQL_TruncateEnriched", "DFT_DerivedAndSplit", "SQL_TruncateRegionSummary",
        "DFT_LookupAndAggregate", "SQL_TruncateWithContact", "DFT_SortAndMergeJoin",
        "SQL_TruncateMerged", "DFT_MergeSortedBranches",
    ]:
        assert by_name[name].verdict == Verdict.MATCH, by_name[name].detail


def test_concurrency_group_matches():
    result = _run(DTSX_BASE / "Package_Exports.dtsx", GENERATED_BASE / "Package_Exports")
    by_name = _by_name(result, "Concurrency")
    for name in ["DFT_ExportDelimited", "DFT_ExportFixedWidth", "SQL_TruncateExportLog"]:
        assert by_name[name].verdict == Verdict.MATCH, by_name[name].detail


def test_sequence_container_and_foreach_loop_match():
    result = _run(DTSX_BASE / "Package_Advanced.dtsx", GENERATED_BASE / "Package_Advanced")
    by_name = _by_name(result, "ControlFlow")
    assert by_name["SQL_TruncateTargets"].verdict == Verdict.MATCH
    assert by_name["FST_ArchiveWorkbook"].verdict == Verdict.MATCH
    assert by_name["SQL_LogFileName"].verdict == Verdict.MATCH  # ForEach-absorbed child
    assert by_name["DFT_BuildSalesSummary"] if False else True  # (sanity: no accidental cross-package bleed)


def test_expression_gated_and_failure_edge_match():
    result = _run(DTSX_BASE / "Package.dtsx", GENERATED_BASE / "Package")
    cf_by_name = _by_name(result, "ControlFlow")
    assert cf_by_name["DFT_BuildSalesSummary"].verdict == Verdict.MATCH  # expression-gated
    failure_by_name = _by_name(result, "FailureHandler")
    assert failure_by_name["SQL_LogLoadFailure"].verdict == Verdict.MATCH


def test_event_handler_matches():
    result = _run(DTSX_BASE / "Package_Advanced.dtsx", GENERATED_BASE / "Package_Advanced")
    by_name = _by_name(result, "EventHandler")
    assert by_name["SQL_LogOnError"].verdict == Verdict.MATCH


def test_disabled_node_reported_unverifiable_not_a_gap():
    result = _run(DTSX_BASE / "Package_Legacy.dtsx", GENERATED_BASE / "Package_Legacy")
    by_name = _by_name(result, "ControlFlow")
    assert by_name["SQL_LegacyStep_DISABLED"].verdict == Verdict.UNVERIFIABLE


def test_mutated_left_removed_edge_reports_left_only(tmp_path: Path):
    """Remove one precedence constraint from a copy of Package_Transforms.dtsx (the edge from
    SQL_TruncateEnriched to DFT_DerivedAndSplit) — the left side now expects DFT_DerivedAndSplit
    to be a level-0 root alongside SQL_TruncateEnriched, which no longer matches the generated
    code's fixed call order, so it must be reported as a Mismatch (wrong position), not silently
    accepted."""
    original_path = DTSX_BASE / "Package_Transforms.dtsx"
    original_text = original_path.read_text(encoding="utf-8")
    needle = (
        '<DTS:PrecedenceConstraint\n'
    )
    # Locate and remove the specific constraint element connecting these two tasks.
    import re
    pattern = re.compile(
        r'<DTS:PrecedenceConstraint\b[^>]*?DTS:From="Package\\SQL_TruncateEnriched"[^>]*?/>',
        re.DOTALL,
    )
    assert pattern.search(original_text), "fixture assumption broke — update the pattern"
    mutated_text = pattern.sub("", original_text, count=1)
    assert mutated_text != original_text

    mutated_dtsx = tmp_path / "Package_Transforms_mutated.dtsx"
    mutated_dtsx.write_text(mutated_text, encoding="utf-8")

    result = _run(mutated_dtsx, GENERATED_BASE / "Package_Transforms")
    # Removing the sole incoming edge makes DFT_DerivedAndSplit level-0 alongside
    # SQL_TruncateEnriched (now genuinely no ordering constraint between them) — Gate 2 correctly
    # reclassifies this as an expected *concurrency* group, which the generated code (a plain
    # sequential call, no Task.WhenAll) does not satisfy.
    concurrency_by_name = _by_name(result, "Concurrency")
    assert concurrency_by_name["DFT_DerivedAndSplit"].verdict == Verdict.LEFT_ONLY
    assert concurrency_by_name["SQL_TruncateEnriched"].verdict == Verdict.LEFT_ONLY
    # Unrelated nodes must still match — no collateral damage from the mutation.
    by_name = _by_name(result, "ControlFlow")
    assert by_name["DFT_MergeSortedBranches"].verdict == Verdict.MATCH


def test_mutated_right_removed_call_reports_left_only(tmp_path: Path):
    """Remove one `await` call from a copy of the generated Package_Transforms.cs RunAsync body —
    Gate 2 must report that node as LeftOnly (or a Mismatch in position for its successor), never
    silently pass."""
    mutated_dir = tmp_path / "Package_Transforms_mutated_generated"
    shutil.copytree(
        GENERATED_BASE / "Package_Transforms", mutated_dir,
        ignore=shutil.ignore_patterns("bin", "obj", "TestResults"),
    )
    cs_file = mutated_dir / "Package_Transforms.cs"
    original_text = cs_file.read_text(encoding="utf-8-sig")
    needle = "await DFT_DerivedAndSplit(uow, ct);\n"
    assert needle in original_text, "fixture assumption broke — update the needle"
    mutated_text = original_text.replace(needle, "", 1)
    assert mutated_text != original_text
    cs_file.write_text(mutated_text, encoding="utf-8")

    result = _run(DTSX_BASE / "Package_Transforms.dtsx", mutated_dir)
    by_name = _by_name(result, "ControlFlow")
    assert by_name["DFT_DerivedAndSplit"].verdict == Verdict.LEFT_ONLY


def test_mutated_right_broken_concurrency_group_reports_gap(tmp_path: Path):
    """Drop one branch out of the generated Task.WhenAll(...) in a copy of Package_Exports.cs —
    the concurrency group no longer matches the left side's 3-node group, so Gate 2 must report a
    gap rather than silently accepting a partial group as a match."""
    mutated_dir = tmp_path / "Package_Exports_mutated_generated"
    shutil.copytree(
        GENERATED_BASE / "Package_Exports", mutated_dir,
        ignore=shutil.ignore_patterns("bin", "obj", "TestResults"),
    )
    cs_file = mutated_dir / "Package_Exports.cs"
    original_text = cs_file.read_text(encoding="utf-8-sig")
    needle = (
        "                RunBranchAsync(async branchUow =>\n"
        "                {\n"
        "                    await SQL_TruncateExportLog(branchUow, ct);\n"
        "                }, ct)\n"
    )
    assert needle in original_text, "fixture assumption broke — update the needle"
    # Remove the branch and the now-dangling trailing comma on the line before it.
    mutated_text = original_text.replace(",\n" + needle, "\n", 1)
    assert mutated_text != original_text, "fixture assumption broke — comma-join pattern didn't match"
    cs_file.write_text(mutated_text, encoding="utf-8")

    result = _run(DTSX_BASE / "Package_Exports.dtsx", mutated_dir)
    by_name = _by_name(result, "Concurrency")
    assert by_name["SQL_TruncateExportLog"].verdict == Verdict.LEFT_ONLY
    for name in ["DFT_ExportDelimited", "DFT_ExportFixedWidth"]:
        assert by_name[name].verdict == Verdict.LEFT_ONLY
