"""Left-side extraction: data-flow (pipeline) internal graph from raw .dtsx XML.

Nodes = pipeline components inside one Microsoft.Pipeline; edges = <path> elements connecting a
named output port to a named input port. See docs/dtsx-graph-notes.md §2.
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
class DataFlowComponent:
    ref_id: str
    name: str
    component_type: str


@dataclass
class DataFlowPath:
    start_component: str   # component name (parsed out of startId)
    start_port: str
    end_component: str
    end_port: str


@dataclass
class DataFlowGraph:
    dft_path: str  # e.g. "Package\\DFT_LoadCustomers"
    components: list[DataFlowComponent] = field(default_factory=list)
    paths: list[DataFlowPath] = field(default_factory=list)


def _parse_port_ref(ref: str) -> tuple[str, str]:
    """"Package\\DFT_X\\CompName.Outputs[Port Name]" -> ("CompName", "Port Name")."""
    if ".Outputs[" in ref:
        comp_part, port_part = ref.split(".Outputs[", 1)
    elif ".Inputs[" in ref:
        comp_part, port_part = ref.split(".Inputs[", 1)
    else:
        return ref.rsplit("\\", 1)[-1], ""
    comp_name = comp_part.rsplit("\\", 1)[-1]
    port_name = port_part[:-1] if port_part.endswith("]") else port_part
    return comp_name, port_name


def extract_pipeline(pipeline_executable, dft_path: str) -> DataFlowGraph | None:
    object_data = next(iter(_direct_children(pipeline_executable, "ObjectData")), None)
    if object_data is None:
        return None
    pipeline_elem = next((c for c in object_data if _local_name(c.tag) == "pipeline"), None)
    if pipeline_elem is None:
        return None

    graph = DataFlowGraph(dft_path=dft_path)

    components_container = next(
        (c for c in pipeline_elem if _local_name(c.tag) == "components"), None
    )
    if components_container is not None:
        for component in components_container:
            if _local_name(component.tag) != "component":
                continue
            graph.components.append(DataFlowComponent(
                ref_id=component.attrib.get("refId", ""),
                name=component.attrib.get("name", ""),
                component_type=_resolve_component_type(component),
            ))

    paths_container = next((c for c in pipeline_elem if _local_name(c.tag) == "paths"), None)
    if paths_container is not None:
        for path in paths_container:
            if _local_name(path.tag) != "path":
                continue
            start_comp, start_port = _parse_port_ref(path.attrib.get("startId", ""))
            end_comp, end_port = _parse_port_ref(path.attrib.get("endId", ""))
            graph.paths.append(DataFlowPath(
                start_component=start_comp, start_port=start_port,
                end_component=end_comp, end_port=end_port,
            ))

    return graph


def extract_all(dtsx_path: str | Path) -> dict[str, DataFlowGraph]:
    """Returns {dft_path: DataFlowGraph} for every Microsoft.Pipeline in the package, at any
    nesting depth."""
    root = parse_dtsx(dtsx_path)
    package = _dts_attr(root, "ObjectName") or Path(dtsx_path).stem
    root_ref_id = root.attrib.get("{www.microsoft.com/SqlServer/Dts}refId") or package
    results: dict[str, DataFlowGraph] = {}

    def walk(owner_elem, owner_path: str) -> None:
        for executables_wrapper in _direct_children(owner_elem, "Executables"):
            for child in _direct_children(executables_wrapper, "Executable"):
                object_name = _dts_attr(child, "ObjectName") or "?"
                child_path = f"{owner_path}\\{object_name}"
                creation_name = _dts_attr(child, "CreationName") or ""
                if _dts_attr(child, "Disabled") == "True":
                    continue
                if creation_name == "Microsoft.Pipeline":
                    graph = extract_pipeline(child, child_path)
                    if graph is not None:
                        results[child_path] = graph
                else:
                    walk(child, child_path)

    walk(root, root_ref_id)
    return results
