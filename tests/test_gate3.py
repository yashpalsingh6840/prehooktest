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

from ssis_validator.codegen.csharp_construct_presence import any_construct_referenced
from ssis_validator.codegen.csharp_schema import extract as extract_right_schema
from ssis_validator.codegen.csharp_sql import extract as extract_right_sql
from ssis_validator.compare.gate3_schema import Verdict, run_gate3
from ssis_validator.dtsx.component_schema import extract as extract_left_schema
from ssis_validator.dtsx.no_translation_registry import NO_TRANSLATION_CONSTRUCTS
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


def _present_constructs(generated_dir: Path) -> frozenset[str]:
    all_names = sorted({n for ns in NO_TRANSLATION_CONSTRUCTS.values() for n in ns})
    return frozenset(n for n in all_names if any_construct_referenced(generated_dir, (n,)))


def _run(dtsx_path: Path, generated_dir: Path):
    left_schema = extract_left_schema(dtsx_path)
    left_sql = extract_left_sql(dtsx_path)
    right_sql = extract_right_sql(generated_dir)
    right_schema = extract_right_schema(generated_dir)
    present = _present_constructs(generated_dir)
    return run_gate3(left_schema, left_sql.sql_items, right_sql.sql_items, right_schema, present)


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
    report a Mismatch for that destination while every other item is untouched.

    The mutation targets the <externalMetadataColumn> (the destination's own declared column),
    not the <inputColumn>'s cachedDataType (the pipeline buffer's pre-write view) — Gate 3
    compares the former, resolved via externalMetadataColumnId, not the latter (see
    component_schema.py's _extract_mapped_input_columns docstring; this distinction is exactly
    what produced 6 confirmed false-positive Mismatch findings before it was fixed, per
    docs/Gate-Report-Findings-2026-09-09.md)."""
    mutated_dtsx = tmp_path / "Package_mutated.dtsx"
    original_text = DTSX_PATH.read_text(encoding="utf-8")
    needle = (
        'refId="Package\\DFT_LoadCustomers\\OLEDST_StagingCustomers.Inputs[OLE DB Destination '
        'Input].ExternalColumns[CustomerID]"\n                      dataType="wstr"'
    )
    assert needle in original_text, "fixture assumption broke — update the needle"
    mutated_text = original_text.replace(
        needle,
        needle.replace('dataType="wstr"', 'dataType="i4"'),
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


PACKAGE_ADVANCED_DTSX = DTSX_PATH.parent / "Package_Advanced.dtsx"
PACKAGE_ADVANCED_GENERATED = GENERATED_DIR.parent / "Package_Advanced"
PACKAGE_TRANSFORMS_DTSX = DTSX_PATH.parent / "Package_Transforms.dtsx"
PACKAGE_TRANSFORMS_GENERATED = GENERATED_DIR.parent / "Package_Transforms"


@pytest.mark.skipif(
    not PACKAGE_ADVANCED_DTSX.exists() or not PACKAGE_ADVANCED_GENERATED.exists(),
    reason="Package_Advanced sample pair not available on this machine.",
)
def test_real_pair_buffer_vs_target_type_coercion_is_match_not_mismatch():
    """Regression test for a confirmed false positive (docs/Gate-Report-Findings-2026-09-09.md):
    OLEDST_Drip.CustomerID's mapped <inputColumn> has cachedDataType="r8" (the Excel source's
    pre-write buffer type), but its externalMetadataColumnId resolves to the destination's real
    column, dataType="i4" — the generated `int` is correct and must resolve to Match, not
    Mismatch against the buffer's r8."""
    result = _run(PACKAGE_ADVANCED_DTSX, PACKAGE_ADVANCED_GENERATED)
    by_name = {f.object_name: f for f in result.findings}
    finding = by_name["OLEDST_Drip"]
    assert finding.verdict == Verdict.MATCH
    assert "CustomerID" in finding.detail


@pytest.mark.skipif(
    not PACKAGE_TRANSFORMS_DTSX.exists() or not PACKAGE_TRANSFORMS_GENERATED.exists(),
    reason="Package_Transforms sample pair not available on this machine.",
)
def test_real_pair_aggregate_buffer_type_coercion_is_match_not_mismatch():
    """Regression test for a confirmed false positive: OLEDST_RegionSummary.CustomerCount's
    mapped <inputColumn> has cachedDataType="ui8" (AGG_ByRegion's Count() output type), but its
    externalMetadataColumnId resolves to the real column, dataType="i4" — the generated `int` is
    correct."""
    result = _run(PACKAGE_TRANSFORMS_DTSX, PACKAGE_TRANSFORMS_GENERATED)
    by_name = {f.object_name: f for f in result.findings}
    assert by_name["OLEDST_RegionSummary"].verdict == Verdict.MATCH


@pytest.mark.skipif(
    not PACKAGE_TRANSFORMS_DTSX.exists() or not PACKAGE_TRANSFORMS_GENERATED.exists(),
    reason="Package_Transforms sample pair not available on this machine.",
)
def test_real_pair_data_conversion_renamed_column_is_match_not_mismatch():
    """Regression test for a confirmed false positive: OLEDST_CustomerEnriched's mapped
    <inputColumn>s carry DCONV_Types's own renamed buffer names (CustomerID_i4, SignupDate_dt),
    but their externalMetadataColumnId resolves to the destination's real column names
    (CustomerID, SignupDate) — matching the generated property names correctly. Verdict is
    NormalizedMatch (not a plain Match) because SignupDate is dbDate -> DateOnly?, the separately
    accepted modern-equivalent mapping — not because of the renamed-column fix under test here."""
    result = _run(PACKAGE_TRANSFORMS_DTSX, PACKAGE_TRANSFORMS_GENERATED)
    by_name = {f.object_name: f for f in result.findings}
    finding = by_name["OLEDST_CustomerEnriched"]
    assert finding.verdict == Verdict.NORMALIZED_MATCH
    assert "CustomerID_i4" not in finding.detail
    assert "SignupDate_dt" not in finding.detail


PACKAGE_EXPORTS_DTSX = DTSX_PATH.parent / "Package_Exports.dtsx"
PACKAGE_EXPORTS_GENERATED = GENERATED_DIR.parent / "Package_Exports"

_NO_TRANSLATION_COMPONENTS = {
    "SORT_Canada": "SortingRowSource",
    "SORT_RestOfWorld": "SortingRowSource",
    "SORT_Contacts": "SortingRowSource",
    "SORT_Customers": "SortingRowSource",
    "UNION_Recombine": "ConcatenatingRowSource",
    "MRG_SortedUnion": "MergeInterleaveRowSource",
    "RC_MatchedRows": "CountingRowSource",
    "RC_UnmatchedRows": "CountingRowSource",
}


@pytest.mark.skipif(
    not PACKAGE_TRANSFORMS_DTSX.exists() or not PACKAGE_TRANSFORMS_GENERATED.exists(),
    reason="Package_Transforms sample pair not available on this machine.",
)
def test_real_pair_no_translation_construct_reports_left_only():
    """Sort, Union All, Merge, and Row Count have no generated counterpart anywhere in
    Package_Transforms — confirmed by direct inspection (zero references to SortingRowSource /
    ConcatenatingRowSource / MergeInterleaveRowSource / CountingRowSource in the whole generated
    project). This is a real, tool-detected gap: LeftOnly, not Unverifiable — proves the "can
    fail" direction using real, naturally-occurring absence (see plan §7.3)."""
    result = _run(PACKAGE_TRANSFORMS_DTSX, PACKAGE_TRANSFORMS_GENERATED)
    by_name = {f.object_name: f for f in result.findings}
    for name in _NO_TRANSLATION_COMPONENTS:
        assert by_name[name].verdict == Verdict.LEFT_ONLY, name
    # An untouched, genuinely-translated internal transform must stay Unverifiable, not get
    # collaterally swept into LeftOnly by this new check.
    assert by_name["LKP_Country"].verdict == Verdict.UNVERIFIABLE


def test_mutated_right_construct_reference_present_keeps_unverifiable(tmp_path: Path):
    """Mutate a *copy* of the generated project: add a reference to SortingRowSource somewhere
    in the project. Gate 3 must then treat Sort as "possibly translated somewhere" and fall back
    to the honest Unverifiable — proving the check is a real presence/absence signal, not a
    verdict that always fires regardless of content (the "can pass" direction, per plan §7.3)."""
    mutated_dir = tmp_path / "Package_Transforms_mutated_generated"
    shutil.copytree(
        PACKAGE_TRANSFORMS_GENERATED, mutated_dir,
        ignore=shutil.ignore_patterns("bin", "obj", "TestResults"),
    )
    marker_file = mutated_dir / "Package_Transforms.cs"
    original_text = marker_file.read_text(encoding="utf-8")
    mutated_text = original_text + "\n// SortingRowSource referenced here for this test\n"
    marker_file.write_text(mutated_text, encoding="utf-8")

    result = _run(PACKAGE_TRANSFORMS_DTSX, mutated_dir)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["SORT_Canada"].verdict == Verdict.UNVERIFIABLE
    assert by_name["SORT_RestOfWorld"].verdict == Verdict.UNVERIFIABLE
    # Untouched constructs (Union All, Merge, Row Count) must still report their real gap —
    # the mutation must not collaterally mask unrelated absences.
    assert by_name["UNION_Recombine"].verdict == Verdict.LEFT_ONLY
    assert by_name["RC_MatchedRows"].verdict == Verdict.LEFT_ONLY


@pytest.mark.skipif(
    not PACKAGE_EXPORTS_DTSX.exists() or not PACKAGE_EXPORTS_GENERATED.exists(),
    reason="Package_Exports sample pair not available on this machine.",
)
def test_real_pair_flatfile_destination_correlates_via_factory_method_name():
    """DFT_ExportDelimited_DST and DFT_ExportFixedWidth_DST are FlatFileDestinations with no
    (schema, table) identity — correlated instead via the enclosing factory method's own name
    matching the SSIS component name (confirmed reliable for these two real instances)."""
    result = _run(PACKAGE_EXPORTS_DTSX, PACKAGE_EXPORTS_GENERATED)
    by_name = {f.object_name: f for f in result.findings}
    assert by_name["DFT_ExportDelimited_DST"].verdict == Verdict.MATCH
    assert by_name["DFT_ExportFixedWidth_DST"].verdict == Verdict.MATCH


@pytest.mark.skipif(
    not PACKAGE_EXPORTS_DTSX.exists() or not PACKAGE_EXPORTS_GENERATED.exists(),
    reason="Package_Exports sample pair not available on this machine.",
)
def test_mutated_right_flatfile_destination_missing_property_reports_mismatch(tmp_path: Path):
    """Mutate a *copy* of the generated project: remove one property from the FlatFileDestination
    correlated class. Gate 3 must report a Mismatch — proving the factory-method-name correlation
    is actually exercised (compares real columns), not merely always resolving to Match."""
    mutated_dir = tmp_path / "Package_Exports_mutated_generated"
    shutil.copytree(
        PACKAGE_EXPORTS_GENERATED, mutated_dir,
        ignore=shutil.ignore_patterns("bin", "obj", "TestResults"),
    )
    model_file = mutated_dir / "Model" / "DFT_ExportDelimited.cs"
    original_text = model_file.read_text(encoding="utf-8")
    needle = '    public string CustomerID { get; set; } = "";\n\n'
    assert needle in original_text, "fixture assumption broke — update the needle"
    mutated_text = original_text.replace(needle, "", 1)
    assert mutated_text != original_text
    model_file.write_text(mutated_text, encoding="utf-8")

    result = _run(PACKAGE_EXPORTS_DTSX, mutated_dir)
    by_name = {f.object_name: f for f in result.findings}

    assert by_name["DFT_ExportDelimited_DST"].verdict == Verdict.MISMATCH
    assert "CustomerID" in by_name["DFT_ExportDelimited_DST"].detail
    # Untouched sibling destination must still match.
    assert by_name["DFT_ExportFixedWidth_DST"].verdict == Verdict.MATCH
