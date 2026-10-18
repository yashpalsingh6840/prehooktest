"""Right-side extraction: declared column shape from generated C#.

Confirmed across multiple real generated files (Model/*.cs, Csv/*Row.cs, Sql/*Row.cs): a
declared schema unit is a plain `sealed class` with only auto-property members (optionally
preceded by an attribute line like `[SsisWidth(20)]`, optionally with a default-value
initializer) — never a base clause, primary constructor, or interface implementation (those
belong to DbContext/Transform classes, which are excluded by requiring `{` immediately after the
class name with no intervening `:`/`(`).

One correlation aid, confirmed by grepping the *construction site* (not the enclosing factory
method's name, which can diverge — e.g. `AGG_ByRegionSource()` wraps a `FilteringRowSource`
constructed with the string literal `"AGG_ByRegion"`) across every real generated package: every
row source, regardless of its concrete kind (Csv/Sql/Excel/FixedWidth/Filtering), is built as
`new <Kind>RowSource<RowType>("ComponentName", ...)`, where the string-literal first argument is
always the SSIS component's own name — the same "generated shape names its artifact via a
string-literal constructor argument, not the enclosing declaration's name" pattern Gate 1 already
relies on for SQL sources (docs/dtsx-xml-notes.md §2). Destination correlation is different:
`IBulkSink<Entity> <Name>(...)`'s `<Name>` is NOT reliably the SSIS destination component's name
(observed as both the bare component name and a table-derived name across the 5 samples), so
destination correlation instead reuses Gate 1's already-proven (schema, table) identity match
(see compare/gate3_schema.py), never a method or constructor name.

For a destination with no (schema, table) identity at all (a non-database destination Gate 1's
registry doesn't cover, e.g. FlatFileDestination), a second, narrower fallback applies: confirmed
against 2 of 3 real FlatFileDestination instances, the *enclosing factory method's own name*
(not its `FlatFileBulkSink<T>(File("..."), ...)` construction site's string argument, which is
the flat-file connection manager's file name, unrelated to the component) equals the SSIS
component's own name — `internal IBulkSink<T> DFT_ExportDelimited_DST()` for SSIS component
`DFT_ExportDelimited_DST`. The third instance (`FFDST_AuditTrail_APPEND`) is constructed inline
inside a `ConditionalSplitBranch` array with no standalone factory method at all — a distinct
shape observed exactly once, not generalized into a heuristic (never guess from a single sample).
This fallback is only ever consulted when the (schema, table) path already failed, so it can
never conflict with or override that proven, more specific correlation.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

_SKIP_DIR_NAMES = {"bin", "obj", "TestResults"}

_CLASS_RE = re.compile(
    r"(?:public|internal)\s+sealed\s+(?:partial\s+)?class\s+(?P<name>\w+)\s*\{(?P<body>.*?)\n\}",
    re.DOTALL,
)
_PROPERTY_RE = re.compile(
    r"public\s+(?P<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*\??(?:\[\])?)\s+(?P<name>\w+)\s*"
    r"\{\s*get;\s*set;\s*\}"
)
_ROW_SOURCE_RE = re.compile(
    r'new\s+\w*RowSource<(?P<row_type>\w+)>\(\s*"(?P<name>[^"]+)"'
)
_BULK_SINK_METHOD_RE = re.compile(
    r"(?:public|internal)\s+I\w*BulkSink<(?P<row_type>\w+)>\s+(?P<name>\w+)\s*\("
)


@dataclass
class PropertyDecl:
    name: str
    clr_type_raw: str


@dataclass
class CsClass:
    name: str
    file_path: str
    properties: list[PropertyDecl] = field(default_factory=list)


@dataclass
class CsharpSchemaIndex:
    classes: dict[str, CsClass] = field(default_factory=dict)
    row_source_methods: dict[str, str] = field(default_factory=dict)  # component name -> row class
    bulk_sink_methods: dict[str, str] = field(default_factory=dict)  # component name -> entity class


def _scan_classes(text: str, file_path: str) -> list[CsClass]:
    classes = []
    for m in _CLASS_RE.finditer(text):
        props = [
            PropertyDecl(name=pm.group("name"), clr_type_raw=pm.group("type"))
            for pm in _PROPERTY_RE.finditer(m.group("body"))
        ]
        if props:
            classes.append(CsClass(name=m.group("name"), file_path=file_path, properties=props))
    return classes


def _scan_row_source_methods(text: str) -> dict[str, str]:
    return {m.group("name"): m.group("row_type") for m in _ROW_SOURCE_RE.finditer(text)}


def _scan_bulk_sink_methods(text: str) -> dict[str, str]:
    return {m.group("name"): m.group("row_type") for m in _BULK_SINK_METHOD_RE.finditer(text)}


def extract(generated_package_dir: str | Path) -> CsharpSchemaIndex:
    root = Path(generated_package_dir)
    index = CsharpSchemaIndex()
    for cs_file in root.rglob("*.cs"):
        if any(part in _SKIP_DIR_NAMES for part in cs_file.parts):
            continue
        text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
        rel_path = str(cs_file.relative_to(root))
        for cls in _scan_classes(text, rel_path):
            index.classes[cls.name] = cls
        index.row_source_methods.update(_scan_row_source_methods(text))
        index.bulk_sink_methods.update(_scan_bulk_sink_methods(text))
    return index
