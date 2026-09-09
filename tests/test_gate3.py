"""Gate 3 (per-component input/output schema parity) proof tests.

Run against the real sample pair (Package.dtsx / gen/generate/Package) per the plan's discipline
(§7): prove the gate reports correct matches on the real data, AND prove it can fail correctly by
comparing against a deliberately mutated copy — never edit an original fixture in place, always
mutate a tmp_path copy.
"""
from __future__ import annotations

import shutil
from pathlib import Path

import pytest

from ssis_validator.codegen.csharp_schema import extract as extract_right_schema
from ssis_validator.codegen.csharp_sql import extract as extract_right_sql
from ssis_validator.compare.gate3_schema import Verdict, run_gate3
from ssis_validator.dtsx.component_schema import extract as extract_left_schema
from ssis_validator.dtsx.sql_extraction import extract as extract_left_sql

DTSX_PATH = Path(
    r"D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx"
)
# This directory is regenerated externally by the generation tool (see tests/test_gate1.py) —
# its content can drift between test runs. Never assert a specific real-world gap here; only
# assert confirmed matches, and prove the Mismatch/LeftOnly paths via deliberate mutation.
GENERATED_DIR = Path(r"D:\tmp\Sandeep_Packages\gen\generate\Package")

pytestmark = pytest.mark.skipif(
    not DTSX_PATH.exists() or not GENERATED_DIR.exists(),
    reason="Sample package pair not available on this machine.",
)


def _run(dtsx_path: Path, generated_dir: Path):
    left_schema = extract_left_schema(dtsx_path)
    left_sql = extract_left_sql(dtsx_path)
    right_sql = extract_right_sql(generated_dir)
    right_schema = extract_right_schema(generated_dir)
    return run_gate3(left_schema, left_sql.sql_items, right_sql.sql_items, right_schema)


def test_real_pair_matches_confirmed_shapes():
    """Known-good matches on the real sample, across every correlation strategy: a source
    (row-source-constructor correlation) and two destinations (schema+table correlation, reused
    from Gate 1)."""
    result = _run(DTSX_PATH, GENERATED_DIR)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["FF_SRC_Customers"].verdict == Verdict.MATCH
    assert by_name["OLEDST_StagingCustomers"].verdict == Verdict.MATCH
    assert by_name["OLEDST_StagingErrors"].verdict == Verdict.MATCH


def test_real_pair_dbdate_as_dateonly_is_normalized_match():
    """OLEDST_SalesSummary.LastOrderDate is dbDate on the left and generated as DateOnly (not
    DateTime) on the right — the documented modern-equivalent acceptance, not a plain Match."""
    result = _run(DTSX_PATH, GENERATED_DIR)
    by_name = {f.object_name: f for f in result.findings}
    finding = by_name["OLEDST_SalesSummary"]
    assert finding.verdict == Verdict.NORMALIZED_MATCH
    assert "LastOrderDate" in finding.detail and "DateOnly" in finding.detail


def test_real_pair_reports_script_components_as_unverifiable():
    result = _run(DTSX_PATH, GENERATED_DIR)
    by_name = {f.object_name: f for f in result.findings}
    assert by_name["SCR_ComputeCustomerTier"].verdict == Verdict.UNVERIFIABLE
    assert by_name["SCR_CleanseCustomerRow"].verdict == Verdict.UNVERIFIABLE
    assert by_name["SCR_ComputeCustomerTier"].scope == "InternalTransform"


def test_mutated_left_column_type_is_reported_as_mismatch(tmp_path: Path):
    """Mutate a *copy* of the .dtsx (never the original): change one mapped destination column's
    declared SSIS type so it no longer maps to the generated property's .NET type. Gate 3 must
    report a Mismatch for that destination while every other item is untouched."""
    mutated_dtsx = tmp_path / "Package_mutated.dtsx"
    original_text = DTSX_PATH.read_text(encoding="utf-8")
    needle = (
        'refId="Package\\DFT_LoadCustomers\\OLEDST_StagingCustomers.Inputs[OLE DB Destination '
        'Input].Columns[CustomerID]"\n                      cachedDataType="wstr"'
    )
    assert needle in original_text, "fixture assumption broke — update the needle"
    mutated_text = original_text.replace(
        needle,
        needle.replace('cachedDataType="wstr"', 'cachedDataType="i4"'),
        1,
    )
    assert mutated_text != original_text
    mutated_dtsx.write_text(mutated_text, encoding="utf-8")

    result = _run(mutated_dtsx, GENERATED_DIR)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["OLEDST_StagingCustomers"].verdict == Verdict.MISMATCH
    assert "CustomerID" in by_name["OLEDST_StagingCustomers"].detail
    # Untouched items must still match — the mutation must not cause collateral false mismatches.
    assert by_name["FF_SRC_Customers"].verdict == Verdict.MATCH
    assert by_name["OLEDST_StagingErrors"].verdict == Verdict.MATCH


def test_mutated_right_missing_property_is_reported_as_mismatch(tmp_path: Path):
    """Mutate a *copy* of the generated project: remove one property from a destination's model
    class. Gate 3 must report a Mismatch (a genuinely-mapped column with no generated property)
    while the rest of the project's findings stay intact."""
    mutated_dir = tmp_path / "Package_mutated_generated"
    shutil.copytree(
        GENERATED_DIR, mutated_dir,
        ignore=shutil.ignore_patterns("bin", "obj", "TestResults"),
    )
    model_file = mutated_dir / "Model" / "StagingCustomers.cs"
    original_text = model_file.read_text(encoding="utf-8")
    needle = '    public string CustomerID { get; set; } = "";\n\n'
    assert needle in original_text, "fixture assumption broke — update the needle"
    mutated_text = original_text.replace(needle, "", 1)
    assert mutated_text != original_text
    model_file.write_text(mutated_text, encoding="utf-8")

    result = _run(DTSX_PATH, mutated_dir)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["OLEDST_StagingCustomers"].verdict == Verdict.MISMATCH
    assert "CustomerID" in by_name["OLEDST_StagingCustomers"].detail
    assert by_name["FF_SRC_Customers"].verdict == Verdict.MATCH
    assert by_name["OLEDST_StagingErrors"].verdict == Verdict.MATCH
