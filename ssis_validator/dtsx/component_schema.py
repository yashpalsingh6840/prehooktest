"""Left-side extraction: per-component declared input/output column shape from raw .dtsx XML.

Confirmed by direct inspection of all 5 sample packages:

- A **source**'s declared produced shape lives in `<externalMetadataColumns>` under its
  `<outputs><output>` — *when populated* (see below).
- A **destination**'s declared shape is NOT its full target-table schema. A destination's
  `<externalMetadataColumns>` lists every column the real target table has, including ones SSIS
  never maps from the pipeline (confirmed empirically: OLEDST_StagingErrors declares `ErrorRowID`
  there — an unmapped identity column — and several packages declare an unmapped `*At` audit
  timestamp, both absent from `<inputColumns>`). A destination is only obligated to be able to
  *write* the columns it actually maps, so its declared shape is built from `<inputColumns>`
  (`cachedName`/`cachedDataType`) instead — the columns the pipeline genuinely sends. A generated
  class legitimately declaring extra properties for unmapped columns (e.g. to model a
  DB-defaulted audit column for EF Core) is not a defect and is not compared here.

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

from ssis_validator.dtsx.sql_extraction import (
    _direct_children,
    _dts_attr,
    _local_name,
    _resolve_component_type,
    parse_dtsx,
)


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
    """A destination's genuinely-mapped shape: the pipeline buffer columns it actually writes,
    not its full target-table schema (see module docstring)."""
    ics = next(iter(_direct_children(input_elem, "inputColumns")), None)
    if ics is None:
        return []
    columns = []
    for col in _direct_children(ics, "inputColumn"):
        name = col.attrib.get("cachedName")
        data_type = col.attrib.get("cachedDataType")
        if name and data_type:
            columns.append(ColumnDecl(name=name, ssis_type_raw=data_type))
    return columns


def _extract_component_schema(component_elem, component_path: str) -> ComponentSchema:
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
    results = []
    for component in components_container:
        if _local_name(component.tag) != "component":
            continue
        comp_name = component.attrib.get("name", "?")
        results.append(_extract_component_schema(component, f"{dft_path}\\{comp_name}"))
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
