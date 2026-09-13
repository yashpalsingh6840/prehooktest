"""Proof tests for find_name_mentions -- the bounded fallback search used when a Gate 3
correlation-failure finding has no known correlate anywhere, not even via Gate 1's own SQL-content
correlation. Run against the real sample generated projects."""
from __future__ import annotations

from pathlib import Path

import pytest

from ssis_validator.codegen.csharp_related_source import find_name_mentions

PACKAGE_LEGACY_GENERATED = Path(r"D:\tmp\Sandeep_Packages\gen\generate\Package_Legacy")

pytestmark = pytest.mark.skipif(
    not PACKAGE_LEGACY_GENERATED.exists(),
    reason="Sample generated project not available on this machine.",
)


def test_find_name_mentions_locates_the_real_flat_file_sink_construction():
    """Real case (Package_Legacy): FFDST_AuditTrail_APPEND has no standalone factory method and
    no Gate 1 SQL correlation (FlatFileDestination carries no SQL) -- its only trace is as a
    type-name substring baked into generated identifiers (DFT_FixedWidthImportFFDST_AuditTrail_APPEND)
    at its actual FlatFileBulkSink construction site inside a ConditionalSplitBranch array."""
    matches = find_name_mentions(PACKAGE_LEGACY_GENERATED, "FFDST_AuditTrail_APPEND", max_matches=5)
    assert matches
    files = {m["file"] for m in matches}
    assert any("Package_Legacy.cs" in f for f in files)
    combined = "\n".join(m["excerpt"] for m in matches)
    assert "FlatFileBulkSink" in combined
    assert "FlatFileColumnFormat" in combined


def test_find_name_mentions_returns_empty_for_a_name_never_mentioned():
    matches = find_name_mentions(PACKAGE_LEGACY_GENERATED, "NoSuchComponentXYZ123", max_matches=5)
    assert matches == []


def test_find_name_mentions_respects_max_matches_cap():
    # "row" appears constantly across generated files -- confirms the cap actually bounds output
    # rather than being advisory only.
    matches = find_name_mentions(PACKAGE_LEGACY_GENERATED, "row", max_matches=2)
    assert len(matches) <= 2
