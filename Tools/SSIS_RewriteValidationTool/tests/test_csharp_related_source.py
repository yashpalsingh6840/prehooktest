"""Proof tests for the additional right-side source lookup discovered while hand-running Phase
C's own verification step: a DFT orchestration excerpt only wires together factory calls -- the
real per-row/per-script logic lives in a sibling same-file factory method or a separate
Mapping//Fills//ScriptTasks file. Run against the real sample generated projects."""
from __future__ import annotations

from pathlib import Path

import pytest

from ssis_validator.codegen.csharp_graph import find_package_cs_file
from ssis_validator.codegen.csharp_related_source import (
    collect_related_sources,
    find_class_source,
    referenced_identifiers,
)

TRANSFORMS_GENERATED = Path(r"D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms")
PACKAGE_GENERATED = Path(r"D:\tmp\Sandeep_Packages\gen\generate\Package")

pytestmark = pytest.mark.skipif(
    not TRANSFORMS_GENERATED.exists() or not PACKAGE_GENERATED.exists(),
    reason="Sample generated projects not available on this machine.",
)


def test_referenced_identifiers_finds_both_new_and_bare_calls():
    excerpt = (
        '        var source = AGG_ByRegion();\n'
        '        var transform = new RegionSummaryTransform();\n'
        '        var sink = OLEDST_RegionSummary();\n'
        '        var step = new DataFlowStep<RegionSummaryAggregateRow, RegionSummary>('
        '"DFT_LookupAndAggregate", source, transform, sink, Log<X>());\n'
    )
    names = referenced_identifiers(excerpt)
    assert "AGG_ByRegion" in names
    assert "RegionSummaryTransform" in names
    assert "OLEDST_RegionSummary" in names
    assert "DataFlowStep" in names
    assert "Log" in names


def test_find_class_source_locates_mapping_class():
    matches = find_class_source(TRANSFORMS_GENERATED, "RegionSummaryTransform")
    assert len(matches) == 1
    file_path, text = matches[0]
    assert file_path.replace("/", "\\") == "Mapping\\RegionSummaryTransform.cs"
    assert "IRowTransform<RegionSummaryAggregateRow, RegionSummary>" in text


def test_find_class_source_returns_empty_for_etl_core_framework_type():
    # SqlBulkSink/DataFlowStep/CsvRowSource are Etl.Core framework types -- no defining file
    # exists inside Package_Transforms' own directory (confirmed: Etl.Core is a sibling project).
    assert find_class_source(TRANSFORMS_GENERATED, "SqlBulkSink") == []
    assert find_class_source(TRANSFORMS_GENERATED, "DataFlowStep") == []


def test_collect_related_sources_recovers_the_real_join_and_filter_logic_for_lookup():
    """This is the exact gap discovered hand-running the AI prompt: LKP_Country/AGG_ByRegion's
    packet excerpt (the DFT orchestration method) never mentions the Lookup's cache or the
    aggregate's group/count logic at all -- it's inside a sibling `AGG_ByRegion()` factory method
    in the SAME file, invisible to the plain DFT-body extractor."""
    cs_file = find_package_cs_file(TRANSFORMS_GENERATED)
    assert cs_file is not None
    own_text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
    excerpt = (
        '        var source = AGG_ByRegion();\n'
        '        var transform = new RegionSummaryTransform();\n'
        '        var sink = OLEDST_RegionSummary();\n'
    )
    related = collect_related_sources(TRANSFORMS_GENERATED, excerpt, own_text, str(cs_file))

    assert "AGG_ByRegion" in related
    agg_source = related["AGG_ByRegion"][0]["source"]
    assert "lKP_CountryCache" in agg_source
    assert "BuildAggregateSource" in agg_source

    assert "RegionSummaryTransform" in related
    assert "IRowTransform" in related["RegionSummaryTransform"][0]["source"]


def test_collect_related_sources_finds_component_own_file_even_when_not_in_excerpt():
    """Real gap found hand-running the AI prompt: DER_Enrich's own generated implementation
    (Mapping/DER_Enrich.cs) is never referenced by name in DER_Enrich's own DFT orchestration
    excerpt -- it's called two levels removed, from inside CSPLIT_Validity_Valid.Map(). Passing
    the component's own name must resolve it directly, independent of excerpt indirection depth."""
    excerpt = (
        '        var source = FF_SRC_Customers();\n'
        '        var step = new ConditionalSplitStep<DFT_DerivedAndSplitCsvRow>(\n'
        '            "DFT_DerivedAndSplit", source, new CSPLIT_ValidityRouter(), [...]);\n'
    )
    related = collect_related_sources(
        TRANSFORMS_GENERATED, excerpt, component_name="DER_Enrich",
    )
    assert "DER_Enrich" in related
    source = related["DER_Enrich"][0]["source"]
    assert "FullName" in source and "EmailDomain" in source and "TenureDays" in source
    assert "TRIM(FirstName)" in source  # the original FriendlyExpression, kept as a comment


def test_collect_related_sources_recovers_script_task_fill():
    cs_file = find_package_cs_file(PACKAGE_GENERATED)
    excerpt = (
        '        var step = new SCR_NotifyAndLogProgressScriptTask(packageVariables, services);\n'
    )
    related = collect_related_sources(
        PACKAGE_GENERATED, excerpt,
        cs_file.read_text(encoding="utf-8-sig", errors="replace") if cs_file else None,
        str(cs_file) if cs_file else None,
    )
    assert "SCR_NotifyAndLogProgressScriptTask" in related
    files = {e["file"] for e in related["SCR_NotifyAndLogProgressScriptTask"]}
    assert any("Fills" in f for f in files)
    fill_source = next(e["source"] for e in related["SCR_NotifyAndLogProgressScriptTask"] if "Fills" in e["file"])
    assert "EtlAuditLog" in fill_source
    assert "RowsLoaded" in fill_source
