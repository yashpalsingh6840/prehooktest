"""Left-side extraction: control-flow graph (nodes, edges, concurrency groups) from raw .dtsx XML.

Structural markers only — see docs/dtsx-graph-notes.md for the empirical evidence behind every
shape handled here.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path

from ssis_validator.dtsx.sql_extraction import _direct_children, _dts_attr, parse_dtsx


class EdgeKind(str, Enum):
    SUCCESS = "Success"           # default: no Value attribute
    FAILURE = "Failure"           # Value="1", inline (main control flow, not an event handler)
    EXPRESSION = "Expression"     # EvalOp + Expression present


@dataclass
class ControlFlowNode:
    path: str                 # backslash refId path, e.g. "Package\\DFT_LoadCustomers"
    object_name: str
    creation_name: str
    container_path: str       # the refId path of the owning container (or "" for package root)
    is_container: bool = False
    disabled: bool = False


@dataclass
class ControlFlowEdge:
    from_path: str
    to_path: str
    kind: EdgeKind
    container_path: str
    expression: str | None = None
    logical_and: bool = True


@dataclass
class EventHandlerNode:
    path: str                  # e.g. "Package.EventHandlers[OnError]\\SQL_LogOnError"
    object_name: str
    event_name: str
    owner_path: str            # the executable path the handler is attached to


@dataclass
class ControlFlowGraph:
    package: str
    nodes: list[ControlFlowNode] = field(default_factory=list)
    edges: list[ControlFlowEdge] = field(default_factory=list)
    disabled_nodes: list[ControlFlowNode] = field(default_factory=list)
    event_handler_nodes: list[EventHandlerNode] = field(default_factory=list)

    def concurrency_groups(self) -> list[list[str]]:
        """Per container, groups of siblings that become "ready" at the same topological level
        (longest-path rank from a root, within that container's own precedence-constraint DAG) —
        e.g. three tasks that all follow the same prior step, with no constraint between one
        another, are concurrent even though they share a common predecessor. A node one level
        down (ordered after one of its same-container siblings) is never included in that
        sibling's group. Confirmed against real generated Task.WhenAll groupings
        (docs/dtsx-graph-notes.md §1.3, §3, §4.3)."""
        groups: list[list[str]] = []
        by_container: dict[str, list[str]] = {}
        for n in self.nodes:
            by_container.setdefault(n.container_path, []).append(n.path)

        for container_path, all_children in by_container.items():
            if len(all_children) < 2:
                continue
            container_edges = [e for e in self.edges if e.container_path == container_path]

            # A node reachable ONLY via Failure-kind edges is a dead-end failure handler — a
            # mutually-exclusive alternate branch that runs only when the normal flow does NOT
            # continue, not a peer that could run concurrently with anything. It belongs to its
            # own category (docs/dtsx-graph-notes.md §4.5), not the normal-flow concurrency
            # computation — otherwise it would be (wrongly) leveled alongside whatever else
            # follows its trigger node.
            failure_only = {
                e.to_path for e in container_edges if e.kind == EdgeKind.FAILURE
            } - {e.from_path for e in container_edges if e.kind != EdgeKind.FAILURE} - {
                e.to_path for e in container_edges if e.kind != EdgeKind.FAILURE
            }
            children = [c for c in all_children if c not in failure_only]
            if len(children) < 2:
                continue

            local_edges = [
                (e.from_path, e.to_path) for e in container_edges
                if e.kind != EdgeKind.FAILURE
            ]
            predecessors: dict[str, list[str]] = {c: [] for c in children}
            for src, dst in local_edges:
                if dst in predecessors:
                    predecessors[dst].append(src)

            level: dict[str, int] = {}

            def compute_level(node: str, stack: tuple[str, ...] = ()) -> int:
                if node in level:
                    return level[node]
                if node in stack:
                    return 0  # cycle guard — shouldn't happen in a valid SSIS control flow
                preds = predecessors.get(node, [])
                level[node] = 0 if not preds else 1 + max(
                    compute_level(p, stack + (node,)) for p in preds
                )
                return level[node]

            for c in children:
                compute_level(c)

            by_level: dict[int, list[str]] = {}
            for c in children:
                by_level.setdefault(level[c], []).append(c)
            for members in by_level.values():
                if len(members) >= 2:
                    groups.append(sorted(members))
        return groups


CONTAINER_CREATION_NAMES = {"Microsoft.Package", "STOCK:SEQUENCE", "STOCK:FOREACHLOOP"}


def _parse_precedence_constraints(
    container_elem, container_path: str
) -> list[ControlFlowEdge]:
    edges = []
    for pc_container in _direct_children(container_elem, "PrecedenceConstraints"):
        for pc in _direct_children(pc_container, "PrecedenceConstraint"):
            from_path = _dts_attr(pc, "From") or ""
            to_path = _dts_attr(pc, "To") or ""
            value = _dts_attr(pc, "Value")
            eval_op = _dts_attr(pc, "EvalOp")
            expression = _dts_attr(pc, "Expression")
            logical_and = (_dts_attr(pc, "LogicalAnd") or "True") == "True"

            if eval_op is not None and expression is not None:
                kind = EdgeKind.EXPRESSION
            elif value == "1":
                kind = EdgeKind.FAILURE
            else:
                kind = EdgeKind.SUCCESS

            edges.append(ControlFlowEdge(
                from_path=from_path, to_path=to_path, kind=kind,
                container_path=container_path, expression=expression, logical_and=logical_and,
            ))
    return edges


def _walk(
    owner_elem, package: str, owner_path: str, graph: ControlFlowGraph
) -> None:
    for executables_wrapper in _direct_children(owner_elem, "Executables"):
        children = _direct_children(executables_wrapper, "Executable")
        for child in children:
            object_name = _dts_attr(child, "ObjectName") or "?"
            child_path = f"{owner_path}\\{object_name}"
            creation_name = _dts_attr(child, "CreationName") or ""
            disabled = _dts_attr(child, "Disabled") == "True"
            is_container = creation_name in CONTAINER_CREATION_NAMES

            node = ControlFlowNode(
                path=child_path, object_name=object_name, creation_name=creation_name,
                container_path=owner_path, is_container=is_container, disabled=disabled,
            )
            if disabled:
                graph.disabled_nodes.append(node)
            else:
                graph.nodes.append(node)
                if is_container:
                    _walk(child, package, child_path, graph)

        graph.edges.extend(_parse_precedence_constraints(owner_elem, owner_path))

    for handlers_wrapper in _direct_children(owner_elem, "EventHandlers"):
        for handler in _direct_children(handlers_wrapper, "EventHandler"):
            event_name = _dts_attr(handler, "EventName") or _dts_attr(handler, "ObjectName") or "?"
            handler_path = f"{owner_path}.EventHandlers[{event_name}]"
            for executables_wrapper in _direct_children(handler, "Executables"):
                for child in _direct_children(executables_wrapper, "Executable"):
                    object_name = _dts_attr(child, "ObjectName") or "?"
                    child_path = f"{handler_path}\\{object_name}"
                    graph.event_handler_nodes.append(EventHandlerNode(
                        path=child_path, object_name=object_name,
                        event_name=event_name, owner_path=owner_path,
                    ))
            graph.edges.extend(_parse_precedence_constraints(handler, handler_path))


def extract(dtsx_path: str | Path) -> ControlFlowGraph:
    root = parse_dtsx(dtsx_path)
    package = _dts_attr(root, "ObjectName") or Path(dtsx_path).stem
    # The root's DTS:refId is always the literal "Package" regardless of DTS:ObjectName — that's
    # what PrecedenceConstraint From/To attributes actually reference (docs/dtsx-graph-notes.md
    # §1.1), not the package's real name. Path-building must use refId, not ObjectName.
    root_ref_id = root.attrib.get("{www.microsoft.com/SqlServer/Dts}refId") or package
    graph = ControlFlowGraph(package=package)
    _walk(root, package, root_ref_id, graph)
    return graph
