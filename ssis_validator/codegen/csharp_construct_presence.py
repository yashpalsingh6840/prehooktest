"""Right-side check: does ANY reference to a specific generated-runtime construct exist anywhere
in the generated project?

Deliberately coarse: a project-wide, whole-text presence/absence signal, not a per-component-
instance structural match. It answers "was this SSIS component type translated using this
construct anywhere in the project at all", not "does this specific component correlate to this
specific construct usage" (that would need deeper join-graph analysis this tool doesn't have —
see compare/gate3_schema.py for how the two-sided result of this check is used: absence is a
real, reportable gap; presence is deliberately NOT treated as proof of a per-instance match, and
those components stay `Unverifiable` rather than being upgraded to a guessed `Match`).
"""
from __future__ import annotations

from pathlib import Path

_SKIP_DIR_NAMES = {"bin", "obj", "TestResults"}


def any_construct_referenced(generated_package_dir: str | Path, construct_names: tuple[str, ...]) -> bool:
    root = Path(generated_package_dir)
    for cs_file in root.rglob("*.cs"):
        if any(part in _SKIP_DIR_NAMES for part in cs_file.parts):
            continue
        text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
        if any(name in text for name in construct_names):
            return True
    return False
