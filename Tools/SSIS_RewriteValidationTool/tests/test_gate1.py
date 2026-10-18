"""Gate 1 (SQL statement & schema parity) proof tests.

Run against the real sample pair (Package.dtsx / gen/generate/Package) per the plan's discipline
(§7): prove the gate reports correct matches on the real data, AND prove it can fail correctly by
comparing against a deliberately mutated copy — never edit an original fixture in place, always
mutate a tmp_path copy.
"""
from __future__ import annotations

import shutil
from pathlib import Path

import pytest

from ssis_validator.codegen.csharp_sql import extract as extract_right
from ssis_validator.compare.gate1_sql import Verdict, run_gate1
from ssis_validator.dtsx.sql_extraction import extract as extract_left

DTSX_PATH = Path(
    r"D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx"
)
# This directory is regenerated externally by the generation tool (its own README says
# "Regenerated every run") — its content can drift between test runs. Never assert a specific
# real-world gap here; only assert confirmed matches, and prove the LeftOnly/Mismatch paths via
# deliberate mutation of a copy instead (see test_mutated_right_table_identity_is_reported_as_mismatch).
GENERATED_DIR = Path(r"D:\tmp\Sandeep_Packages\gen\generate\Package")

pytestmark = pytest.mark.skipif(
    not DTSX_PATH.exists() or not GENERATED_DIR.exists(),
    reason="Sample package pair not available on this machine.",
)


def _run(dtsx_path: Path, generated_dir: Path):
    left = extract_left(dtsx_path)
    right = extract_right(generated_dir)
    return run_gate1(left.sql_items, right.sql_items)


def test_real_pair_matches_confirmed_shapes():
    """Known-good matches on the real sample: proves the mechanism recognizes a true match."""
    result = _run(DTSX_PATH, GENERATED_DIR)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["SQL_TruncateTargets"].verdict == Verdict.MATCH
    assert by_name["OLESRC_OrdersAndDetails"].verdict == Verdict.MATCH
    assert by_name["OLEDST_SalesSummary"].verdict == Verdict.MATCH
    assert by_name["OLEDST_StagingCustomers"].verdict == Verdict.MATCH
    assert by_name["OLEDST_StagingErrors"].verdict == Verdict.MATCH
    assert by_name["SQL_LogLoadFailure"].verdict == Verdict.MATCH


def test_real_pair_reports_script_components_as_unverifiable():
    result = _run(DTSX_PATH, GENERATED_DIR)
    by_name = {f.object_name: f for f in result.findings}
    assert by_name["SCR_ComputeCustomerTier"].verdict == Verdict.UNVERIFIABLE
    assert by_name["SCR_CleanseCustomerRow"].verdict == Verdict.UNVERIFIABLE


def test_mutated_left_sql_text_is_reported_as_mismatch(tmp_path: Path):
    """Mutate a *copy* of the .dtsx (never the original): change one Execute SQL Task's literal
    SQL text. Gate 1 must report a Mismatch for that task while every other item is untouched."""
    mutated_dtsx = tmp_path / "Package_mutated.dtsx"
    original_text = DTSX_PATH.read_text(encoding="utf-8")
    needle = "TRUNCATE TABLE dbo.StagingCustomers;"
    assert needle in original_text, "fixture assumption broke — update the needle"
    mutated_text = original_text.replace(needle, "TRUNCATE TABLE dbo.SomeOtherTableEntirely;", 1)
    assert mutated_text != original_text
    mutated_dtsx.write_text(mutated_text, encoding="utf-8")

    result = _run(mutated_dtsx, GENERATED_DIR)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["SQL_TruncateTargets"].verdict == Verdict.MISMATCH
    assert "SomeOtherTableEntirely" in by_name["SQL_TruncateTargets"].detail
    # Untouched items must still match — the mutation must not cause collateral false mismatches.
    assert by_name["OLESRC_OrdersAndDetails"].verdict == Verdict.MATCH
    assert by_name["OLEDST_SalesSummary"].verdict == Verdict.MATCH


PACKAGE_ADVANCED_DTSX = DTSX_PATH.parent / "Package_Advanced.dtsx"
PACKAGE_ADVANCED_GENERATED = GENERATED_DIR.parent / "Package_Advanced"

pytestmark_advanced = pytest.mark.skipif(
    not PACKAGE_ADVANCED_DTSX.exists() or not PACKAGE_ADVANCED_GENERATED.exists(),
    reason="Package_Advanced sample pair not available on this machine.",
)


@pytestmark_advanced
def test_property_expression_statement_is_normalized_match():
    """SQL_LogFileName's SQL comes from a DTS:PropertyExpression (a runtime SSIS expression),
    compared against the generated BuildStatement(string) overload — an exact-text match is
    impossible since the runtime-varying part differs (an SSIS variable ref vs. a C# parameter
    name); it must still resolve to NormalizedMatch via literal-segment comparison, not Mismatch
    or LeftOnly."""
    result = _run(PACKAGE_ADVANCED_DTSX, PACKAGE_ADVANCED_GENERATED)
    by_name = {f.object_name: f for f in result.findings}
    assert by_name["SQL_LogFileName"].verdict == Verdict.NORMALIZED_MATCH


@pytestmark_advanced
def test_oledbcommand_correlates_by_name_fallback_and_placeholder_normalization():
    """OLECMD_SetFlag (the SSIS component) and DFT_FlagCustomers (the generated OleDbCommandStep,
    keyed by the owning pipeline's name instead) must still resolve to one NormalizedMatch finding
    via the SQL-text fallback correlator, with the SSIS '?' marker matched against .NET's '{0}'."""
    result = _run(PACKAGE_ADVANCED_DTSX, PACKAGE_ADVANCED_GENERATED)
    by_name = {f.object_name: f for f in result.findings}
    assert "OLECMD_SetFlag" not in by_name or by_name["OLECMD_SetFlag"].verdict != Verdict.LEFT_ONLY
    assert "DFT_FlagCustomers" not in by_name or by_name["DFT_FlagCustomers"].verdict != Verdict.RIGHT_ONLY
    matched = [
        f for f in result.findings
        if f.left is not None and f.left.object_name == "OLECMD_SetFlag"
    ]
    assert len(matched) == 1
    assert matched[0].verdict == Verdict.NORMALIZED_MATCH
    assert matched[0].right.object_name == "DFT_FlagCustomers"


def test_mutated_right_table_identity_is_reported_as_mismatch(tmp_path: Path):
    """Mutate a *copy* of the generated project's DbContext: rename a destination table. Gate 1
    must report the affected OLEDBDestination as a mismatch (or LeftOnly, if the rename makes it
    unmatchable) while leaving the rest of the project's findings intact."""
    mutated_dir = tmp_path / "Package_mutated_generated"
    shutil.copytree(
        GENERATED_DIR, mutated_dir,
        ignore=shutil.ignore_patterns("bin", "obj", "TestResults"),
    )
    db_context = mutated_dir / "Model" / "PackageDbContext.cs"
    original_text = db_context.read_text(encoding="utf-8")
    needle = 'entity.ToTable("SalesSummary", "dbo");'
    assert needle in original_text, "fixture assumption broke — update the needle"
    mutated_text = original_text.replace(
        needle, 'entity.ToTable("SalesSummaryRenamedByMistake", "dbo");', 1
    )
    db_context.write_text(mutated_text, encoding="utf-8")

    result = _run(DTSX_PATH, mutated_dir)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["OLEDST_SalesSummary"].verdict == Verdict.LEFT_ONLY
    assert by_name["OLEDST_StagingCustomers"].verdict == Verdict.MATCH
    assert by_name["SQL_TruncateTargets"].verdict == Verdict.MATCH
