"""Phase C proof tests: the `ai-context` CLI subcommand and its packet builder. Run against the
real sample pairs — never a hand-authored fixture (see tests/test_gate3.py's own rationale)."""
from __future__ import annotations

import json
from pathlib import Path

import pytest

from ssis_validator.cli import run_ai_context

DTSX_DIR = Path(r"D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL")
GENERATED_ROOT = Path(r"D:\tmp\Sandeep_Packages\gen\generate")

TRANSFORMS_DTSX = DTSX_DIR / "Package_Transforms.dtsx"
TRANSFORMS_GENERATED = GENERATED_ROOT / "Package_Transforms"
PACKAGE_DTSX = DTSX_DIR / "Package.dtsx"
PACKAGE_GENERATED = GENERATED_ROOT / "Package"

pytestmark = pytest.mark.skipif(
    not TRANSFORMS_DTSX.exists() or not TRANSFORMS_GENERATED.exists()
    or not PACKAGE_DTSX.exists() or not PACKAGE_GENERATED.exists(),
    reason="Sample package pairs not available on this machine.",
)


def test_ai_context_emits_one_packet_pair_per_genuinely_semantic_finding(tmp_path: Path):
    exit_code = run_ai_context(str(TRANSFORMS_DTSX), str(TRANSFORMS_GENERATED), str(tmp_path))
    assert exit_code == 0

    json_path = tmp_path / "Package_Transforms.LKP_Country.ai-context.json"
    md_path = tmp_path / "Package_Transforms.LKP_Country.ai-context.md"
    assert json_path.exists() and md_path.exists()

    packet = json.loads(json_path.read_text(encoding="utf-8"))
    assert packet["scope"] == "DataFlowComponent"
    assert packet["component_name"] == "LKP_Country"
    assert packet["evidence"]["sql_command"] == "SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup"
    assert packet["generated_method_name"] == "DFT_LookupAndAggregate"
    assert packet["generated_method_excerpt"] is not None
    assert "AGG_ByRegion" in packet["generated_method_excerpt"]

    # A component type with no registered semantic extractor (e.g. Multicast) and every LeftOnly
    # structural gap (Sort/UnionAll/Merge/RowCount, already resolved by Phase A) must NOT get a
    # packet -- Tier 2 is scoped to genuinely irreducible findings only, never a dumping ground.
    assert not (tmp_path / "Package_Transforms.SORT_Canada.ai-context.json").exists()
    assert not (tmp_path / "Package_Transforms.MCAST_Matched.ai-context.json").exists()


def test_ai_context_includes_script_component_and_script_task_packets(tmp_path: Path):
    exit_code = run_ai_context(str(PACKAGE_DTSX), str(PACKAGE_GENERATED), str(tmp_path))
    assert exit_code == 0

    script_component = json.loads(
        (tmp_path / "Package.SCR_ComputeCustomerTier.ai-context.json").read_text(encoding="utf-8")
    )
    assert script_component["scope"] == "DataFlowComponent"
    assert script_component["evidence"]["files"][0]["file_name"] == "main.cs"
    assert "CustomerTier" in script_component["evidence"]["files"][0]["source_code"]

    script_task = json.loads(
        (tmp_path / "Package.SCR_NotifyAndLogProgress.ai-context.json").read_text(encoding="utf-8")
    )
    assert script_task["scope"] == "ScriptTask"
    assert script_task["component_path"] is None
    assert script_task["evidence"]["files"][0]["file_name"] in ("ScriptMain.vb", "ScriptMain.cs")


def test_combined_mode_deduplicates_shared_related_sources(tmp_path: Path):
    """Real, measured duplication this mode fixes: DER_Enrich, DER_TagInvalid, DER_TagValid, and
    CSPLIT_Validity all belong to the same ConditionalSplitStep and each embed identical copies
    of CSPLIT_ValidityRouter.cs/CSPLIT_Validity_Valid.cs/CSPLIT_Validity_Invalid.cs in
    per-component mode. Combined mode must store each shared file exactly once."""
    exit_code = run_ai_context(
        str(TRANSFORMS_DTSX), str(TRANSFORMS_GENERATED), str(tmp_path), combined=True,
    )
    assert exit_code == 0

    assert not list(tmp_path.glob("Package_Transforms.DER_Enrich.ai-context.json"))

    combined = json.loads((tmp_path / "Package_Transforms.ai-context.json").read_text(encoding="utf-8"))
    assert combined["package"] == "Package_Transforms"
    assert {c["component_name"] for c in combined["components"]} == {
        "CSPLIT_Validity", "DER_Enrich", "DER_TagInvalid", "DER_TagValid",
        "AGG_ByRegion", "LKP_Country", "CSPLIT_ByCountry", "MRG_CustomerContacts",
    }

    router_paths = [p for p in combined["sources"] if p.endswith("CSPLIT_ValidityRouter.cs")]
    assert len(router_paths) == 1  # stored once, not once per referencing component

    der_enrich = next(c for c in combined["components"] if c["component_name"] == "DER_Enrich")
    assert any(f.endswith("CSPLIT_Validity_Valid.cs") for f in der_enrich["related_source_files"])
    # The component's own evidence (its FriendlyExpressions) is untouched by the restructuring.
    assert der_enrich["evidence"]["columns"][0]["output_name"] == "FullName"


def test_combined_mode_is_smaller_than_the_sum_of_per_component_files(tmp_path: Path):
    per_component_dir = tmp_path / "per_component"
    combined_dir = tmp_path / "combined"
    run_ai_context(str(TRANSFORMS_DTSX), str(TRANSFORMS_GENERATED), str(per_component_dir))
    run_ai_context(str(TRANSFORMS_DTSX), str(TRANSFORMS_GENERATED), str(combined_dir), combined=True)

    per_component_total = sum(
        f.stat().st_size for f in per_component_dir.glob("*.ai-context.json")
    )
    combined_total = (combined_dir / "Package_Transforms.ai-context.json").stat().st_size
    assert combined_total < per_component_total


def test_ai_context_markdown_twin_is_self_contained(tmp_path: Path):
    run_ai_context(str(TRANSFORMS_DTSX), str(TRANSFORMS_GENERATED), str(tmp_path))
    markdown = (tmp_path / "Package_Transforms.LKP_Country.ai-context.md").read_text(encoding="utf-8")
    assert "## Left-side (SSIS) semantic evidence" in markdown
    assert "## Right-side (generated C#) method excerpt" in markdown
    assert "CountryLookup" in markdown
    assert "AGG_ByRegion" in markdown
