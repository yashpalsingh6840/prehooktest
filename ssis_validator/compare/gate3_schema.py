"""Gate 3 — per-component input/output schema parity.

Declared shape only, never actual data (per the plan, §4): for every pipeline component with a
real, external declared column schema (a source's produced shape, or a destination's target
shape), compares column names and SSIS-type-to-.NET-type mapping against the generated
counterpart's class properties.

Correlation strategy (see codegen/csharp_schema.py for the empirical basis):
- destination-input side: correlated via (schema, table) identity, reusing the exact same
  left/right SQL-extraction items Gate 1 already builds and proves — never re-derived, so Gate 1
  and Gate 3 always agree on which generated class backs a given destination.
- source-output side: correlated by the SSIS component's own name against the generated
  `IRowSource<T> <Name>(...)` factory method name (a reliable 1:1 key, confirmed empirically).
- A component with no populated externalMetadataColumns on either side (a pure pipeline-internal
  transform — Derived Column, Lookup, Script Component, etc.) has no generated declared-shape
  counterpart to compare (it collapses into constructor arguments/local functions of another
  method — docs/dtsx-graph-notes.md §6) and is reported `Unverifiable`, not a silent pass — UNLESS
  its componentClassID is in `dtsx/no_translation_registry.py` and none of that type's expected
  Etl.Core construct(s) are referenced anywhere in the generated project at all, in which case it
  is a real, tool-detected gap (`LeftOnly`), not an unresolvable ambiguity — see
  `codegen/csharp_construct_presence.py`.
- A non-database destination (no (schema, table) identity Gate 1's registry gives us) falls back
  to a second correlation path: an enclosing factory method whose own name equals the SSIS
  component's name (`codegen/csharp_schema.py`'s `bulk_sink_methods` index) — confirmed reliable
  for 2 of 3 real FlatFileDestination instances; the third has no standalone factory method at
  all and stays honestly `Unverifiable`.

Evidence enrichment (Phase B): an `InternalTransform` `Unverifiable` finding for a component type
with a registered dtsx/semantic_extraction.py extractor carries a structured `evidence` payload
(FriendlyExpression / decoded sort key / resolved lineage / raw enum value with its own
`description` text / Script source) instead of being a bare "no shape to compare" message. This
never changes the verdict — Tier 1 still cannot judge business-logic equivalence — it only makes
the finding self-contained for a human or the Tier 2 AI-assisted adjudication step (see
SSIS-Rewrite-Validation-Plan.md's two-tier architecture) without re-reading the raw .dtsx.

Resolved false-positive history: an earlier version compared each mapped `<inputColumn>`'s own
pipeline-buffer `cachedName`/`cachedDataType` instead of resolving through its
`externalMetadataColumnId` to the destination's actual declared column — see
component_schema.py's `_extract_mapped_input_columns` docstring. That produced 6 confirmed false
Mismatch findings across 3 packages (an Excel/Aggregate/ADO.NET buffer-vs-target type coercion,
and a Data Conversion Transformation's renamed buffer column, e.g. `CustomerID` ->
`CustomerID_i4`, reaching a destination) — documented and traced in
docs/Gate-Report-Findings-2026-09-09.md before the fix.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass, field
from enum import Enum

from ssis_validator.codegen.csharp_schema import CsClass, CsharpSchemaIndex
from ssis_validator.codegen.csharp_sql import CodegenSqlItem
from ssis_validator.dtsx.component_schema import ComponentSchema, ComponentSchemaSide
from ssis_validator.dtsx.no_translation_registry import NO_TRANSLATION_CONSTRUCTS
from ssis_validator.dtsx.schema_types import normalize_clr_type, resolve_ssis_type
from ssis_validator.dtsx.sql_extraction import SqlItem, SqlKind


class Verdict(str, Enum):
    MATCH = "Match"
    NORMALIZED_MATCH = "NormalizedMatch"
    MISMATCH = "Mismatch"
    LEFT_ONLY = "LeftOnly"
    UNVERIFIABLE = "Unverifiable"


@dataclass
class Finding:
    scope: str  # "SourceOutput" | "DestinationInput" | "InternalTransform"
    object_name: str
    verdict: Verdict
    detail: str
    component_path: str = ""
    # Populated only for InternalTransform findings whose component type has a registered
    # semantic_extraction.py extractor (Phase B) -- a self-contained, already-resolved evidence
    # packet (FriendlyExpression / decoded sort key / resolved lineage / raw enum + its own
    # `description` text / Script source), so a surviving Unverifiable finding never requires
    # re-reading the raw .dtsx to make sense of it. None when no extractor is registered for this
    # component type (still Unverifiable, just without added evidence).
    evidence: object | None = None


@dataclass
class Gate3Result:
    findings: list[Finding] = field(default_factory=list)


def _left_table_items_by_path(left_sql_items: list[SqlItem]) -> dict[str, SqlItem]:
    return {
        item.executable_path: item
        for item in left_sql_items
        if item.kind == SqlKind.TABLE_IDENTITY
    }


def _right_table_items_by_table(right_sql_items: list[CodegenSqlItem]) -> dict[str, CodegenSqlItem]:
    idx: dict[str, CodegenSqlItem] = {}
    for item in right_sql_items:
        if item.kind == SqlKind.TABLE_IDENTITY and item.table_name:
            idx[item.table_name.lower()] = item
    return idx


def _compare_columns(side: ComponentSchemaSide, cls: CsClass) -> tuple[Verdict, str]:
    """For a destination-input side, `side.columns` is already scoped to genuinely-mapped
    columns (see component_schema.py) — a generated class declaring extra properties beyond
    those (e.g. to model a DB-defaulted audit column for EF Core) is expected, not a defect, so
    right-only names are informational only for that role. For a source-output side, the
    generated row type is expected to declare exactly the source's own produced columns, so an
    extra property there is a real, reportable discrepancy (a fabricated column)."""
    strict_right = side.role == "source-output"
    left_by_name = {c.name.lower(): c for c in side.columns}
    right_by_name = {p.name.lower(): p for p in cls.properties}

    left_only_names = [n for n in left_by_name if n not in right_by_name]
    right_only_names = [n for n in right_by_name if n not in left_by_name]

    lines = []
    mismatches = []
    unverifiable_names = []
    normalized_names = []

    for name in sorted(set(left_by_name) & set(right_by_name)):
        left_col = left_by_name[name]
        right_prop = right_by_name[name]
        resolved = resolve_ssis_type(left_col.ssis_type_raw)
        right_norm = normalize_clr_type(right_prop.clr_type_raw)
        if resolved is None:
            unverifiable_names.append(left_col.name)
            lines.append(
                f"  ? {left_col.name}: SSIS type {left_col.ssis_type_raw!r} not in the documented "
                f"DT_* enumeration — cannot verify against generated type {right_prop.clr_type_raw!r}."
            )
            continue
        if right_norm in resolved.clr_candidates:
            if right_norm != resolved.clr_candidates[0]:
                normalized_names.append(left_col.name)
                lines.append(
                    f"  ~ {left_col.name}: SSIS {left_col.ssis_type_raw!r} -> {right_prop.clr_type_raw!r} "
                    f"(accepted modern equivalent of {resolved.clr_candidates[0]!r})."
                )
            else:
                lines.append(f"  = {left_col.name}: SSIS {left_col.ssis_type_raw!r} -> {right_prop.clr_type_raw!r}.")
        else:
            mismatches.append(left_col.name)
            lines.append(
                f"  x {left_col.name}: SSIS {left_col.ssis_type_raw!r} expects one of "
                f"{resolved.clr_candidates!r}, generated type is {right_prop.clr_type_raw!r}."
            )

    for name in left_only_names:
        lines.append(f"  x {left_by_name[name].name}: declared on the left, no matching generated property.")
    for name in right_only_names:
        marker = "x" if strict_right else "i"
        note = "generated property has no matching left-side column."
        if not strict_right:
            note += " Not blocking — a destination may legitimately model an unmapped column."
        lines.append(f"  {marker} {right_by_name[name].name}: {note}")

    detail = "\n".join(lines) if lines else "No columns declared on either side."

    if mismatches or left_only_names or (strict_right and right_only_names):
        return Verdict.MISMATCH, detail
    comparable = len(left_by_name) - len(unverifiable_names)
    if left_by_name and comparable == 0:
        return Verdict.UNVERIFIABLE, detail
    if normalized_names:
        return Verdict.NORMALIZED_MATCH, detail
    return Verdict.MATCH, detail


def run_gate3(
    component_schemas: list[ComponentSchema],
    left_sql_items: list[SqlItem],
    right_sql_items: list[CodegenSqlItem],
    csharp_index: CsharpSchemaIndex,
    present_constructs: frozenset[str] = frozenset(),
) -> Gate3Result:
    result = Gate3Result()
    left_tables_by_path = _left_table_items_by_path(left_sql_items)
    right_tables_by_table = _right_table_items_by_table(right_sql_items)

    for schema in component_schemas:
        if not schema.sides:
            construct_names = NO_TRANSLATION_CONSTRUCTS.get(schema.component_type)
            if construct_names and not (set(construct_names) & present_constructs):
                result.findings.append(Finding(
                    scope="InternalTransform", object_name=schema.component_name,
                    verdict=Verdict.LEFT_ONLY,
                    component_path=schema.component_path,
                    detail=(
                        f"'{schema.component_name}' ({schema.component_type}) has no generated "
                        f"counterpart: none of {construct_names!r} — the Etl.Core construct(s) "
                        "that would represent this SSIS component type if translated — are "
                        "referenced anywhere in the generated project. A project-wide presence "
                        "check, not a per-instance match: a real, tool-detected gap, not a "
                        "naming ambiguity."
                    ),
                ))
                continue
            result.findings.append(Finding(
                scope="InternalTransform", object_name=schema.component_name,
                verdict=Verdict.UNVERIFIABLE,
                component_path=schema.component_path,
                evidence=schema.semantic_evidence,
                detail=(
                    f"'{schema.component_name}' ({schema.component_type}) has no populated "
                    "externalMetadataColumns on either side — a pipeline-internal component with "
                    "no generated declared-shape counterpart of its own (see "
                    "docs/dtsx-graph-notes.md §6)."
                    + (
                        " Semantic evidence attached (see `evidence`) for a human/AI reviewer to "
                        "judge the generated logic against."
                        if schema.semantic_evidence is not None else ""
                    )
                ),
            ))
            continue

        for side in schema.sides:
            scope = "DestinationInput" if side.role == "destination-input" else "SourceOutput"

            if side.role == "destination-input":
                left_table_item = left_tables_by_path.get(schema.component_path)
                if left_table_item is None:
                    # Gate 1's sql_extraction only classifies OLE DB/ADO.NET destinations as a
                    # TABLE_IDENTITY item (database, EF-mappable) — a destination type outside
                    # that registry (e.g. OLEDBCommand, FlatFileDestination) has no (schema,
                    # table) identity to correlate by. Fall back to a factory-method-name match
                    # (confirmed reliable for FlatFileDestination when a standalone method
                    # exists — see csharp_schema.py's bulk_sink_methods docstring) before giving
                    # up honestly.
                    cls_name = csharp_index.bulk_sink_methods.get(schema.component_name)
                    if cls_name is None:
                        result.findings.append(Finding(
                            scope=scope, object_name=schema.component_name, verdict=Verdict.UNVERIFIABLE,
                            component_path=schema.component_path,
                            # The declared column shape itself -- not a schema/class correlate,
                            # but real left-side evidence a Tier 2 correlation-assist packet can
                            # use directly instead of re-deriving it from the raw .dtsx (see
                            # report/ai_context.py's build_correlation_gap_packets).
                            evidence=[asdict(c) for c in side.columns],
                            detail=(
                                f"'{schema.component_name}' ({schema.component_type}) is not a "
                                "database destination Gate 1 can identify by (schema, table), and "
                                "no generated factory method is named after this component either "
                                f"— no schema-correlation mechanism matched "
                                f"({len(side.columns)} declared column(s) not compared)."
                            ),
                        ))
                        continue
                else:
                    right_item = (
                        right_tables_by_table.get(left_table_item.table_name.lower())
                        if left_table_item.table_name else None
                    )
                    cls_name = right_item.object_name if right_item else None
            else:
                cls_name = csharp_index.row_source_methods.get(schema.component_name)

            if cls_name is None:
                result.findings.append(Finding(
                    scope=scope, object_name=schema.component_name, verdict=Verdict.LEFT_ONLY,
                    detail=(
                        f"No generated counterpart found for '{schema.component_name}' "
                        f"({schema.component_type}) {side.role} on port {side.port_name!r} — "
                        f"{len(side.columns)} declared column(s) unaccounted for on the right."
                    ),
                ))
                continue

            cls = csharp_index.classes.get(cls_name)
            if cls is None:
                result.findings.append(Finding(
                    scope=scope, object_name=schema.component_name, verdict=Verdict.UNVERIFIABLE,
                    detail=(
                        f"Correlated generated class {cls_name!r} but could not extract its "
                        "properties as a plain declared-shape class."
                    ),
                ))
                continue

            verdict, detail = _compare_columns(side, cls)
            result.findings.append(Finding(
                scope=scope, object_name=schema.component_name, verdict=verdict,
                detail=f"vs. generated class {cls_name!r} ({cls.file_path}):\n{detail}",
            ))

    return result
