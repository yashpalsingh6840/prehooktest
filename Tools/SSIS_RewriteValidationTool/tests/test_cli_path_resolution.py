"""Proof for `cli._resolve_generated_dir` -- pure path logic, no real sample pair needed.

Real case this guards against (2026-09-14): a user ran `ai-context --dtsx Package_Advanced.dtsx
--generated .../generate` (the shared parent holding every package's own subfolder) instead of
`--generated .../generate/Package_Advanced`. That silently produced either no orchestration file
at all (Gate 2 skipped) or, for the extractors that scan recursively, evidence contaminated with
every other package's generated code -- never a loud, obvious error.
"""
from __future__ import annotations

from pathlib import Path

from ssis_validator.cli import _resolve_generated_dir


def test_returns_dir_unchanged_when_it_already_has_its_own_cs_file(tmp_path: Path):
    pkg_dir = tmp_path / "Package_Advanced"
    pkg_dir.mkdir()
    (pkg_dir / "Package_Advanced.cs").write_text("// orchestration", encoding="utf-8")

    resolved = _resolve_generated_dir(str(pkg_dir), "Package_Advanced")
    assert resolved == str(pkg_dir)


def test_auto_corrects_a_shared_parent_root_to_the_named_package_subfolder(tmp_path: Path, capsys):
    root = tmp_path / "generate"
    for name in ["Package", "Package_Advanced", "Package_Legacy"]:
        pkg_dir = root / name
        pkg_dir.mkdir(parents=True)
        (pkg_dir / f"{name}.cs").write_text("// orchestration", encoding="utf-8")

    resolved = _resolve_generated_dir(str(root), "Package_Advanced")
    assert resolved == str(root / "Package_Advanced")
    assert "using that instead" in capsys.readouterr().out


def test_leaves_path_untouched_when_neither_shape_matches(tmp_path: Path):
    """A genuinely wrong path, or a package with no orchestration .cs at all -- not this
    function's job to guess further; the existing find_package_cs_file None-handling covers it."""
    empty_dir = tmp_path / "nothing_here"
    empty_dir.mkdir()

    resolved = _resolve_generated_dir(str(empty_dir), "Package_Advanced")
    assert resolved == str(empty_dir)
