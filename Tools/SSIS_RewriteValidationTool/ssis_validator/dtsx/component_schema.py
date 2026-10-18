"""Left-side extraction: per-component declared input/output column shape from raw .dtsx XML.

Confirmed by direct inspection of all 5 sample packages:

- A **source**'s declared produced shape lives in `<externalMetadataColumns>` under its
  `<outputs><output>` — *when populated* (see below).
- A **destination**'s declared shape is NOT its full target-table schema. A destination's
  `<externalMetadataColumns>` lists every column the real target table has, including ones SSIS
  never maps from the pipeline (confirmed empirically: OLEDST_StagingErrors declares `ErrorRowID`
  there — an unmapped identity column — and several packages declare an unmapped `*At` audit
  timestamp, both absent from `<inputColumns>`). A destination is only obligated to be able to
  *write* the columns it actually maps, so its declared shape is scoped to the `<inputColumns>`
  subset — the columns the pipeline genuinely sends. A generated class legitimately declaring
  extra properties for unmapped columns (e.g. to model a DB-defaulted audit column for EF Core)
  is not a defect and is not compared here.
- For each mapped `<inputColumn>`, the compared name/type come from the `<externalMetadataColumn>`
  its `externalMetadataColumnId` resolves to — the destination's own declared column identity —
  never from the `<inputColumn>`'s own `cachedName`/`cachedDataType`, which is the *pipeline
  buffer's pre-write* view and can legitimately differ from the real target column: confirmed
  empirically across 3 real cases (an Excel source's `r8` buffer for a real `i4` target column;
  an Aggregate's `ui8` `Count()` output for a real `i4` target column; a Data Conversion
  transform's renamed buffer column `CustomerID_i4` for a real target column named `CustomerID`).
  Using the buffer's cached fields instead of the resolved external identity produced 6 confirmed
  false-positive Mismatch findings before this was fixed — see
  docs/Gate-Report-Findings-2026-09-09.md.

Every component also carries an `<externalMetadataColumns>` wrapper even when it's a pure
pipeline-internal pass-through (e.g. a Lookup's reference input); those are empty placeholders
(no `<externalMetadataColumn>` children, no `isUsed="True"`), so source classification is driven
by non-empty children, never by the mere presence of the wrapper tag or by a component-type
allowlist. This makes the classification fully structural: any future component type that ever
populates externalMetadataColumns is picked up automatically, and one that never does (a pure
transform) is honestly reported as `internal-transform` without needing a registry entry for it.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from ssis_validator.dtsx.data_flow import DataFlowPath, _parse_port_ref
from ssis_validator.dtsx.semantic_extraction import (
    extract_aggregate,
    extract_conditional_split,
    extract_derived_column,
    extract_lookup,
    extract_merge_join,
    extract_script_component_source,
    extract_sort,
)
from ssis_validator.dtsx.sql_extraction import (
    _direct_children,
    _dts_attr,
    _local_name,
    _resolve_component_type,
    parse_dtsx,
)

# Dispatch by the resolved component type (see sql_extraction._resolve_component_type -- a Script
# Component's componentClassID is always "Microsoft.ManagedComponentHost", resolved to the more
# specific "Microsoft.ScriptComponentHost" via its own UserComponentTypeName property first).
_SEMANTIC_EXTRACTORS = {
    "Microsoft.DerivedColumn": lambda comp, paths: extract_derived_column(comp),
    "Microsoft.ConditionalSplit": lambda comp, paths: extract_conditional_split(comp),
    "Microsoft.Lookup": lambda comp, paths: extract_lookup(comp, paths),
    "Microsoft.Aggregate": lambda comp, paths: extract_aggregate(comp),
    "Microsoft.MergeJoin": lambda comp, paths: extract_merge_join(comp),
    "Microsoft.Sort": lambda comp, paths: extract_sort(comp),
    "Microsoft.ScriptComponentHost": lambda comp, paths: extract_script_component_source(comp),
}


@dataclass
class ColumnDecl:
    name: str
    ssis_type_raw: str


@dataclass
class ComponentSchemaSide:
    role: str  # "source-output" | "destination-input"
    port_name: str
    columns: list[ColumnDecl] = field(default_factory=list)


@dataclass
class ComponentSchema:
    component_path: str
    component_name: str
    component_type: str
    sides: list[ComponentSchemaSide] = field(default_factory=list)
    # Populated only when `sides` is empty (a pure pipeline-internal transform) and the component
    # type has a registered extractor in semantic_extraction.py -- gives Gate 3's otherwise-bare
    # Unverifiable finding a self-contained, already-resolved evidence packet (Phase B).
    semantic_evidence: object | None = None


def _extract_external_columns(container_elem) -> list[ColumnDecl]:
    emc = next(iter(_direct_children(container_elem, "externalMetadataColumns")), None)
    if emc is None:
        return []
    columns = []
    for col in _direct_children(emc, "externalMetadataColumn"):
        name = col.attrib.get("name")
        data_type = col.attrib.get("dataType")
        if name and data_type:
            columns.append(ColumnDecl(name=name, ssis_type_raw=data_type))
    return columns


def _extract_mapped_input_columns(input_elem) -> list[ColumnDecl]:
    """A destination's genuinely-mapped shape: the columns it actually writes (limited to the
    `<inputColumns>` subset, not its full target-table schema), each identified by its resolved
    `<externalMetadataColumn>` (the real destination column), not by its own pipeline-buffer
    `cachedName`/`cachedDataType` (see module docstring)."""
    emc = next(iter(_direct_children(input_elem, "externalMetadataColumns")), None)
    emc_by_ref: dict[str, object] = {}
    if emc is not None:
        for col in _direct_children(emc, "externalMetadataColumn"):
            ref = col.attrib.get("refId")
            if ref:
                emc_by_ref[ref] = col

    ics = next(iter(_direct_children(input_elem, "inputColumns")), None)
    if ics is None:
        return []
    columns = []
    for col in _direct_children(ics, "inputColumn"):
        target = emc_by_ref.get(col.attrib.get("externalMetadataColumnId", ""))
        if target is not None:
            name = target.attrib.get("name")
            data_type = target.attrib.get("dataType")
        else:
            # Defensive fallback for an inputColumn with no resolvable external identity (not
            # observed in any real sample) -- better to compare the pipeline buffer's own view
            # than to silently drop the column.
            name = col.attrib.get("cachedName")
            data_type = col.attrib.get("cachedDataType")
        if name and data_type:
            columns.append(ColumnDecl(name=name, ssis_type_raw=data_type))
    return columns


def _extract_component_schema(
    component_elem, component_path: str, paths: list[DataFlowPath] = ()
) -> ComponentSchema:
    schema = ComponentSchema(
        component_path=component_path,
        component_name=component_elem.attrib.get("name", ""),
        component_type=_resolve_component_type(component_elem),
    )
    inputs_container = next(iter(_direct_children(component_elem, "inputs")), None)
    if inputs_container is not None:
        for input_elem in _direct_children(inputs_container, "input"):
            # Presence of a populated externalMetadataColumns is what makes this a genuine
            # external destination (vs. a pipeline-internal pass-through input, e.g. a Lookup's
            # reference input) -- but the *compared* shape is the mapped inputColumns subset
            # (see module docstring: an unmapped target column is not this component's job).
            if not _extract_external_columns(input_elem):
                continue
            columns = _extract_mapped_input_columns(input_elem)
            schema.sides.append(ComponentSchemaSide(
                role="destination-input",
                port_name=input_elem.attrib.get("name", ""),
                columns=columns,
            ))
    outputs_container = next(iter(_direct_children(component_elem, "outputs")), None)
    if outputs_container is not None:
        for output_elem in _direct_children(outputs_container, "output"):
            if output_elem.attrib.get("isErrorOut") == "true":
                continue
            columns = _extract_external_columns(output_elem)
            if columns:
                schema.sides.append(ComponentSchemaSide(
                    role="source-output",
                    port_name=output_elem.attrib.get("name", ""),
                    columns=columns,
                ))
    if not schema.sides:
        extractor = _SEMANTIC_EXTRACTORS.get(schema.component_type)
        if extractor is not None:
            schema.semantic_evidence = extractor(component_elem, paths)
    return schema


def _extract_pipeline_component_schemas(pipeline_executable, dft_path: str) -> list[ComponentSchema]:
    object_data = next(iter(_direct_children(pipeline_executable, "ObjectData")), None)
    if object_data is None:
        return []
    pipeline_elem = next((c for c in object_data if _local_name(c.tag) == "pipeline"), None)
    if pipeline_elem is None:
        return []
    components_container = next(
        (c for c in pipeline_elem if _local_name(c.tag) == "components"), None
    )
    if components_container is None:
        return []

    paths_container = next((c for c in pipeline_elem if _local_name(c.tag) == "paths"), None)
    paths: list[DataFlowPath] = []
    if paths_container is not None:
        for path_elem in paths_container:
            if _local_name(path_elem.tag) != "path":
                continue
            start_comp, start_port = _parse_port_ref(path_elem.attrib.get("startId", ""))
            end_comp, end_port = _parse_port_ref(path_elem.attrib.get("endId", ""))
            paths.append(DataFlowPath(
                start_component=start_comp, start_port=start_port,
                end_component=end_comp, end_port=end_port,
            ))

    results = []
    for component in components_container:
        if _local_name(component.tag) != "component":
            continue
        comp_name = component.attrib.get("name", "?")
        results.append(_extract_component_schema(component, f"{dft_path}\\{comp_name}", paths))
    return results


def extract(dtsx_path: str | Path) -> list[ComponentSchema]:
    """Returns every pipeline component's declared column schema, at any nesting depth. Disabled
    executables are excluded (they generate zero C#, per docs/dtsx-graph-notes.md)."""
    root = parse_dtsx(dtsx_path)
    package = _dts_attr(root, "ObjectName") or Path(dtsx_path).stem
    root_ref_id = root.attrib.get("{www.microsoft.com/SqlServer/Dts}refId") or package
    results: list[ComponentSchema] = []

    def walk(owner_elem, owner_path: str) -> None:
        for executables_wrapper in _direct_children(owner_elem, "Executables"):
            for child in _direct_children(executables_wrapper, "Executable"):
                object_name = _dts_attr(child, "ObjectName") or "?"
                child_path = f"{owner_path}\\{object_name}"
                if _dts_attr(child, "Disabled") == "True":
                    continue
                creation_name = _dts_attr(child, "CreationName") or ""
                if creation_name == "Microsoft.Pipeline":
                    results.extend(_extract_pipeline_component_schemas(child, child_path))
                else:
                    walk(child, child_path)

    walk(root, root_ref_id)
    return results
