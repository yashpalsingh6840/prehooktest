"""Phase B semantic-extraction proof tests. Run against the real sample .dtsx files (never a
hand-authored fixture) so every assertion is directly traceable to a specific line the developer
read during Phase B research — see ssis_validator/dtsx/semantic_extraction.py's module docstring.
"""
from __future__ import annotations

import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

from ssis_validator.dtsx.semantic_extraction import (
    extract_aggregate,
    extract_all_script_task_sources,
    extract_conditional_split,
    extract_derived_column,
    extract_lookup,
    extract_merge_join,
    extract_script_component_source,
    extract_script_task_source,
    extract_sort,
    resolve_lineage,
)
from ssis_validator.dtsx.sql_extraction import _local_name, parse_dtsx

TRANSFORMS_DTSX = Path(
    r"D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Transforms.dtsx"
)
PACKAGE_DTSX = Path(
    r"D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx"
)

pytestmark = pytest.mark.skipif(
    not TRANSFORMS_DTSX.exists() or not PACKAGE_DTSX.exists(),
    reason="Sample package pair not available on this machine.",
)


def _find_component(dtsx_path: Path, component_name: str) -> ET.Element:
    root = parse_dtsx(dtsx_path)

    def walk(elem):
        if _local_name(elem.tag) == "component" and elem.attrib.get("name") == component_name:
            return elem
        for child in elem:
            found = walk(child)
            if found is not None:
                return found
        return None

    found = walk(root)
    assert found is not None, f"component {component_name!r} not found in {dtsx_path}"
    return found


def _pipeline_paths(dtsx_path: Path, dft_name: str):
    from ssis_validator.dtsx.data_flow import extract_all
    graphs = extract_all(dtsx_path)
    match = next(g for path, g in graphs.items() if path.endswith(f"\\{dft_name}"))
    return match.paths


def test_resolve_lineage_parses_component_and_column():
    ref = r"Package\DFT_LookupAndAggregate\LKP_Country.Outputs[Lookup Match Output].Columns[Region]"
    assert resolve_lineage(ref) == ("LKP_Country", "Region")
    wrapped = "#{" + ref + "}"
    assert resolve_lineage(wrapped) == ("LKP_Country", "Region")
    assert resolve_lineage(None) is None
    assert resolve_lineage("") is None
    assert resolve_lineage("not-a-lineage-ref") is None


def test_derived_column_friendly_expressions_match_real_sample():
    comp = _find_component(TRANSFORMS_DTSX, "DER_Enrich")
    result = extract_derived_column(comp)
    by_name = {c.output_name: c.friendly_expression for c in result.columns}
    assert by_name["FullName"] == '(DT_WSTR,200)(TRIM(FirstName) + " " + TRIM(LastName))'
    assert by_name["TenureDays"] == 'ISNULL(SignupDate_dt) ? -1 : DATEDIFF("dd",SignupDate_dt,GETDATE())'
    assert "EmailDomain" in by_name


def test_conditional_split_branches_and_default_output():
    comp = _find_component(TRANSFORMS_DTSX, "CSPLIT_Validity")
    result = extract_conditional_split(comp)
    by_name = {o.output_name: o for o in result.outputs}
    valid = by_name["Valid"]
    assert valid.is_default is False
    assert valid.evaluation_order == 0
    assert valid.friendly_expression == '!ISNULL(CustomerID_i4) && FINDSTRING(TRIM(Email),"@",1) > 0'
    invalid = by_name["Invalid"]
    assert invalid.is_default is True
    assert invalid.friendly_expression is None


def test_lookup_semantics_full_shape_including_no_match_routing():
    comp = _find_component(TRANSFORMS_DTSX, "LKP_Country")
    paths = _pipeline_paths(TRANSFORMS_DTSX, "DFT_LookupAndAggregate")
    result = extract_lookup(comp, paths)

    assert result.sql_command == "SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup"
    assert result.connection_type is not None and result.connection_type.raw_value == "0"
    assert result.cache_type is not None and result.cache_type.raw_value == "0"
    assert result.no_match_behavior is not None and result.no_match_behavior.raw_value == "1"
    assert "matching entries" in result.no_match_behavior.description

    join = {j.input_column: j.join_to_reference_column for j in result.join_columns}
    assert join["Country"] == "CountryName"

    out = {o.name: o.copy_from_reference_column for o in result.output_columns}
    assert out["CountryCode"] == "CountryCode"
    assert out["Region"] == "Region"

    # The concrete, checkable answer to "are no-match rows silently dropped?" -- both outputs
    # route somewhere real, and neither destination is a database/file sink (confirmed separately
    # via Gate 3: RC_MatchedRows/RC_UnmatchedRows are dead-end RowCount counters with no further
    # downstream path in this sample).
    assert result.match_output_destination == "MCAST_Matched"
    assert result.no_match_output_destination == "RC_UnmatchedRows"


def test_aggregate_semantics_resolves_source_lineage():
    comp = _find_component(TRANSFORMS_DTSX, "AGG_ByRegion")
    result = extract_aggregate(comp)
    by_name = {c.name: c for c in result.columns}

    region = by_name["Region"]
    assert region.aggregation_type.raw_value == "0"
    assert region.source_component == "LKP_Country"
    assert region.source_column == "Region"

    count = by_name["CustomerCount"]
    assert count.aggregation_type.raw_value == "1"
    assert count.source_component == "FF_SRC_Customers2"
    assert count.source_column == "CustomerID"


def test_merge_join_semantics_join_type_and_output_lineage():
    comp = _find_component(TRANSFORMS_DTSX, "MRG_CustomerContacts")
    result = extract_merge_join(comp)

    assert result.join_type.raw_value == "2"
    assert result.num_key_columns == 1
    assert result.treat_nulls_as_equal is True

    left_names = {c.name for c in result.input_columns if c.side == "left"}
    right_names = {c.name for c in result.input_columns if c.side == "right"}
    assert left_names == {"CustomerID_i4", "Email"}
    assert right_names == {"CustomerID_i4", "ContactPhone", "PreferredChannel"}

    out = {c.name: c.source_column for c in result.output_columns}
    assert out["Email"] == "Email"
    assert out["ContactPhone"] == "ContactPhone"


def test_sort_semantics_decodes_documented_sign_convention():
    comp = _find_component(TRANSFORMS_DTSX, "SORT_Canada")
    result = extract_sort(comp)
    by_name = {c.name: c for c in result.columns}

    assert by_name["CustomerID_i4"].sort_key_position == 1
    assert by_name["CustomerID_i4"].direction == "ascending"
    assert by_name["Country"].sort_key_position == 0
    assert by_name["Country"].direction == "not-sorted"


def test_script_component_source_keeps_only_main_cs():
    comp = _find_component(PACKAGE_DTSX, "SCR_ComputeCustomerTier")
    result = extract_script_component_source(comp)
    names = [f.file_name for f in result.files]
    assert names == ["main.cs"]
    assert "Row.CustomerTier" in result.files[0].source_code
    assert "ComponentWrapper" not in result.files[0].source_code


def test_script_task_source_keeps_only_scriptmain():
    root = parse_dtsx(PACKAGE_DTSX)

    def find_script_projects(elem):
        found = []
        if _local_name(elem.tag) == "ScriptProject":
            found.append(elem)
        for child in elem:
            found.extend(find_script_projects(child))
        return found

    projects = find_script_projects(root)
    assert projects, "expected at least one ScriptTask in Package.dtsx"

    all_files = []
    for project in projects:
        all_files.extend(extract_script_task_source(project).files)

    names = sorted(f.file_name for f in all_files)
    assert names == ["ScriptMain.cs", "ScriptMain.vb"]
    combined = "\n".join(f.source_code for f in all_files)
    assert "EtlAuditLog" in combined or "RowsLoaded" in combined


def test_extract_all_script_task_sources_finds_every_script_task_by_name():
    result = extract_all_script_task_sources(PACKAGE_DTSX)
    assert set(result.keys()) == {"SCR_NotifyAndLogProgress", "SCR_ValidateAndLogStart"}
    for semantics in result.values():
        assert semantics.files
        assert semantics.files[0].file_name in ("ScriptMain.cs", "ScriptMain.vb")
