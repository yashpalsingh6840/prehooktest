"""Gate 2 — control-flow / data-flow graph parity.

Two-tier comparison, per docs/dtsx-graph-notes.md §6.1 (node-granularity mismatch is real):

1. Control-flow level: every top-level executable (task/container/DFT) is a node, 1:1 with a
   generated `await <Name>(...)` call site (or its ForEach-absorbed/failure-handler equivalent).
   Compared per-container: expected topological levels (from precedence constraints) vs. the
   generated code's sequential call list / Task.WhenAll concurrency groups.
2. Data-flow-internal level: components inside one Data Flow Task never get their own call site
   (docs/dtsx-graph-notes.md §4.4) — collapsed into one owning method's body. We do not yet have a
   structural extractor for that method's internal constructor-argument/local-function shape, so
   this level is reported as a scoped `Unverifiable` presence check (does the component's name
   appear at all in its owning DFT's generated method body), never a false Match/Mismatch claim —
   *unless* Gate 3 already ran its own real per-component schema comparison for the same
   component and reached a stronger verdict, in which case this defers to that instead of
   re-reporting the weaker text-presence signal (see `_compare_data_flow_internals`'s docstring;
   confirmed real case, Package_Advanced, 2026-09-13, three components independently `Match` in
   Gate 3 while still sitting `Unverifiable` here before this cross-reference existed).
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum

from ssis_validator.codegen.csharp_graph import CsharpControlFlowGraph, _extract_method_bodies
from ssis_validator.dtsx.control_flow import ControlFlowGraph, EdgeKind
from ssis_validator.dtsx.data_flow import DataFlowGraph


class Verdict(str, Enum):
    MATCH = "Match"
    MISMATCH = "Mismatch"
    LEFT_ONLY = "LeftOnly"
    RIGHT_ONLY = "RightOnly"
    UNVERIFIABLE = "Unverifiable"


@dataclass
class Finding:
    scope: str          # e.g. "ControlFlow", "Concurrency", "FailureHandler", "DataFlow:<dft>"
    object_name: str
    verdict: Verdict
    detail: str


@dataclass
class Gate2Result:
    findings: list[Finding] = field(default_factory=list)


def _short_name(path: str) -> str:
    return path.rsplit("\\", 1)[-1]


def _container_levels(graph: ControlFlowGraph, container_path: str) -> list[list[str]]:
    """Same topological-leveling method as ControlFlowGraph.concurrency_groups, but returns
    every level (including singletons), in ascending order, as short object names."""
    children = [n.path for n in graph.nodes if n.container_path == container_path]
    if not children:
        return []
    container_edges = [e for e in graph.edges if e.container_path == container_path]
    failure_only = {
        e.to_path for e in container_edges if e.kind == EdgeKind.FAILURE
    } - {e.from_path for e in container_edges if e.kind != EdgeKind.FAILURE} - {
        e.to_path for e in container_edges if e.kind != EdgeKind.FAILURE
    }
    children = [c for c in children if c not in failure_only]
    if not children:
        return []
    local_edges = [(e.from_path, e.to_path) for e in container_edges if e.kind != EdgeKind.FAILURE]
    predecessors: dict[str, list[str]] = {c: [] for c in children}
    for src, dst in local_edges:
        if dst in predecessors:
            predecessors[dst].append(src)

    level: dict[str, int] = {}

    def compute(node: str, stack: tuple[str, ...] = ()) -> int:
        if node in level:
            return level[node]
        if node in stack:
            return 0
        preds = predecessors.get(node, [])
        level[node] = 0 if not preds else 1 + max(compute(p, stack + (node,)) for p in preds)
        return level[node]

    for c in children:
        compute(c)

    by_level: dict[int, list[str]] = {}
    for c in children:
        by_level.setdefault(level[c], []).append(_short_name(c))
    return [sorted(by_level[k]) for k in sorted(by_level)]


def _failure_only_nodes(graph: ControlFlowGraph, container_path: str) -> list[str]:
    container_edges = [e for e in graph.edges if e.container_path == container_path]
    normal_from = {e.from_path for e in container_edges if e.kind != EdgeKind.FAILURE}
    normal_to = {e.to_path for e in container_edges if e.kind != EdgeKind.FAILURE}
    failure_targets = {e.to_path for e in container_edges if e.kind == EdgeKind.FAILURE}
    return sorted(failure_targets - normal_from - normal_to)


def _right_method_for_container(container_short_name: str, right: CsharpControlFlowGraph) -> str:
    """The package root's own method is always "RunAsync"; a nested container's is its own
    object name (docs/dtsx-graph-notes.md §4.4)."""
    return "RunAsync" if container_short_name == "Package" else container_short_name


def _compare_container(
    graph: ControlFlowGraph, container_path: str, right: CsharpControlFlowGraph,
    right_groups_remaining: list[list[str]], result: Gate2Result,
) -> set[str]:
    container_short = _short_name(container_path)
    right_method = _right_method_for_container(container_short, right)

    levels = _container_levels(graph, container_path)
    # A mutable worklist we pop matched/consumed names from, rather than a fixed cursor index —
    # a cursor silently desyncs whenever a concurrency-group level fails to match (its members
    # may still be sitting in this plain sequential list) and every later singleton check then
    # compares against the wrong position. Popping keeps every check anchored to what's actually
    # still unaccounted for, regardless of what happened at a prior level.
    right_sequence = list(right.sequential_children.get(right_method, []))

    for level_members in levels:
        if len(level_members) == 1:
            name = level_members[0]
            if name in right.guarded_nodes:
                result.findings.append(Finding(
                    scope="ControlFlow", object_name=name, verdict=Verdict.MATCH,
                    detail=f"Expression-gated node found as a guarded call (guard: {right.guarded_nodes[name]!r}).",
                ))
                continue
            if right.foreach_absorbed_children.get(right_method) == name:
                result.findings.append(Finding(
                    scope="ControlFlow", object_name=name, verdict=Verdict.MATCH,
                    detail=(
                        f"ForEach Loop's single child task is absorbed into {right_method}'s "
                        "ForEachLoopStep lambda rather than getting its own call site — matched "
                        "by the referenced BuildStatement class name."
                    ),
                ))
                continue
            if right_sequence and right_sequence[0] == name:
                result.findings.append(Finding(
                    scope="ControlFlow", object_name=name, verdict=Verdict.MATCH,
                    detail=f"Sequential call found in expected position in {right_method}.",
                ))
                right_sequence.pop(0)
            elif name in right_sequence:
                actual_index = right_sequence.index(name)
                result.findings.append(Finding(
                    scope="ControlFlow", object_name=name, verdict=Verdict.MISMATCH,
                    detail=(
                        f"Node {name!r} is called in {right_method}, but not in the expected "
                        f"order (expected next, found at offset {actual_index} instead; "
                        f"remaining sequence: {right_sequence!r})."
                    ),
                ))
                right_sequence.pop(actual_index)
            else:
                result.findings.append(Finding(
                    scope="ControlFlow", object_name=name, verdict=Verdict.LEFT_ONLY,
                    detail=f"No call site found for {name!r} in generated method {right_method!r}.",
                ))
        else:
            match_idx = next(
                (i for i, g in enumerate(right_groups_remaining) if set(g) == set(level_members)),
                None,
            )
            if match_idx is not None:
                right_groups_remaining.pop(match_idx)
                for name in level_members:
                    result.findings.append(Finding(
                        scope="Concurrency", object_name=name, verdict=Verdict.MATCH,
                        detail=f"Part of a matched Task.WhenAll concurrency group: {sorted(level_members)!r}.",
                    ))
            else:
                for name in level_members:
                    if name in right_sequence:
                        right_sequence.remove(name)
                    result.findings.append(Finding(
                        scope="Concurrency", object_name=name, verdict=Verdict.LEFT_ONLY,
                        detail=(
                            f"Expected a concurrency group {sorted(level_members)!r} (no ordering "
                            f"constraint between these siblings) but no matching Task.WhenAll "
                            f"group was found in generated code. Actual groups found: "
                            f"{right.concurrency_groups!r}."
                        ),
                    ))

    accounted = {n for level in levels for n in level}
    for extra in right_sequence:
        if extra not in accounted:
            result.findings.append(Finding(
                scope="ControlFlow", object_name=extra, verdict=Verdict.RIGHT_ONLY,
                detail=f"Generated call to {extra!r} in {right_method!r} has no corresponding left-side node.",
            ))
    failure_only = _failure_only_nodes(graph, container_path)
    for path in failure_only:
        name = _short_name(path)
        if name in right.failure_handler_names:
            result.findings.append(Finding(
                scope="FailureHandler", object_name=name, verdict=Verdict.MATCH,
                detail="Failure-only node found inlined in the generated catch-block handler set.",
            ))
        else:
            result.findings.append(Finding(
                scope="FailureHandler", object_name=name, verdict=Verdict.LEFT_ONLY,
                detail=f"Failure-only node {name!r} has no matching entry in the generated catch-block handler set {right.failure_handler_names!r}.",
            ))

    return accounted | {_short_name(p) for p in failure_only}


def _compare_event_handlers(graph: ControlFlowGraph, right: CsharpControlFlowGraph, result: Gate2Result) -> None:
    for handler_node in graph.event_handler_nodes:
        name = handler_node.object_name
        if name in right.failure_handler_names:
            result.findings.append(Finding(
                scope="EventHandler", object_name=name, verdict=Verdict.MATCH,
                detail=f"{handler_node.event_name} event-handler task found inlined in the generated catch-block handler set.",
            ))
        else:
            result.findings.append(Finding(
                scope="EventHandler", object_name=name, verdict=Verdict.LEFT_ONLY,
                detail=f"{handler_node.event_name} event-handler task {name!r} has no matching entry in the generated catch-block handler set.",
            ))


def _compare_disabled(graph: ControlFlowGraph, result: Gate2Result) -> None:
    for node in graph.disabled_nodes:
        result.findings.append(Finding(
            scope="ControlFlow", object_name=node.object_name, verdict=Verdict.UNVERIFIABLE,
            detail="DTS:Disabled=\"True\" — excluded from the comparable node/edge set; confirmed to have no generated counterpart by design, not treated as a gap.",
        ))


_GATE3_TO_GATE2_VERDICT = {
    "Match": Verdict.MATCH,
    "NormalizedMatch": Verdict.MATCH,
    "Mismatch": Verdict.MISMATCH,
    "LeftOnly": Verdict.LEFT_ONLY,
}


def _compare_data_flow_internals(
    dft_graphs: dict[str, DataFlowGraph], package_cs_text: str, result: Gate2Result,
    gate3_verdicts: dict[str, str] | None = None,
) -> None:
    """Scoped presence check by default — see module docstring: a name-presence match alone can
    only ever be corroborating evidence, never a real verdict, since the generator may legitimately
    name a component's counterpart after its target table instead.

    `gate3_verdicts` (component_name -> Gate 3's own Verdict.value string, plain strings so this
    module has no import-time dependency on compare/gate3_schema.py) is a deliberate exception:
    when Gate 3 already ran its OWN structural comparison for a component's declared column shape
    and reached Match/NormalizedMatch/Mismatch/LeftOnly, that is real, independent evidence — not
    the same "text happens to appear nearby" signal this function otherwise relies on — so this
    defers to it rather than re-reporting the weaker presence-only signal for a component Gate 3
    already resolved. Confirmed real case (Package_Advanced, 2026-09-13): EXCEL_SRC_Drip,
    OLEDST_Drip, and OLESRC_CustomerIds were all independently `Match` in Gate 3 while sitting
    here as `Unverifiable` — the two gates simply weren't talking to each other. A Gate 3
    `Unverifiable` verdict (no stronger claim available there either) or a component Gate 3 never
    reports on at all (e.g. it never populates an external column shape) still falls through to
    the unchanged presence-only check below."""
    bodies = _extract_method_bodies(package_cs_text)
    gate3_verdicts = gate3_verdicts or {}
    for dft_path, dfg in dft_graphs.items():
        dft_name = _short_name(dft_path)
        body = bodies.get(dft_name)
        scope = f"DataFlow:{dft_name}"
        if body is None:
            for comp in dfg.components:
                result.findings.append(Finding(
                    scope=scope, object_name=comp.name, verdict=Verdict.UNVERIFIABLE,
                    detail=f"Could not locate a generated method body for Data Flow Task {dft_name!r} to check component presence.",
                ))
            continue
        for comp in dfg.components:
            gate3_verdict_str = gate3_verdicts.get(comp.name)
            upgraded = _GATE3_TO_GATE2_VERDICT.get(gate3_verdict_str) if gate3_verdict_str else None
            if upgraded is not None:
                result.findings.append(Finding(
                    scope=scope, object_name=comp.name, verdict=upgraded,
                    detail=(
                        f"Deferring to Gate 3's own per-component schema comparison for this "
                        f"component, which independently reached {gate3_verdict_str!r} — stronger, "
                        "real evidence than this scope's plain name-presence check (see the Gate 3 "
                        "report for the actual column-level comparison)."
                    ),
                ))
                continue
            # A negative result here is NOT evidence of a gap: destination/transform components
            # are frequently named after their target table rather than the SSIS component
            # (docs/dtsx-graph-notes.md §4.4 — confirmed, e.g. "SalesSummaryTransform" for a
            # Script Component, "SalesSummaryDestination()" for an OLEDBDestination). A plain
            # name-presence check can only ever be corroborating evidence, never a Match/LeftOnly
            # verdict, until a real per-component-shape extractor exists.
            found = comp.name in body
            result.findings.append(Finding(
                scope=scope, object_name=comp.name, verdict=Verdict.UNVERIFIABLE,
                detail=(
                    f"{'Component name found' if found else 'Component name NOT found'} by text "
                    f"presence inside {dft_name}'s generated method — not a structural check of "
                    "its data-flow shape; the generator may legitimately name this component's "
                    "generated counterpart after its target table instead. Full data-flow-"
                    "internal parity is not yet implemented."
                ),
            ))


def run_gate2(
    graph: ControlFlowGraph,
    dft_graphs: dict[str, DataFlowGraph],
    right: CsharpControlFlowGraph,
    package_cs_text: str,
    gate3_verdicts: dict[str, str] | None = None,
) -> Gate2Result:
    result = Gate2Result()

    container_paths = {"Package"}
    for n in graph.nodes:
        if n.is_container:
            container_paths.add(n.path)

    # Shared across all containers: a concurrency group (Task.WhenAll site) belongs to exactly
    # one container, but we don't know which until we try each — so all containers draw from one
    # pool, and whatever's left after every container has had a chance is genuinely unmatched.
    right_groups_remaining = [list(g) for g in right.concurrency_groups]
    accounted_names: set[str] = set()

    for container_path in sorted(container_paths, key=len):
        accounted_names |= set(
            _compare_container(graph, container_path, right, right_groups_remaining, result)
        )

    for group in right_groups_remaining:
        for extra in group:
            if extra not in accounted_names:
                result.findings.append(Finding(
                    scope="Concurrency", object_name=extra, verdict=Verdict.RIGHT_ONLY,
                    detail=f"Generated Task.WhenAll branch {extra!r} has no corresponding left-side concurrency group.",
                ))

    _compare_event_handlers(graph, right, result)
    _compare_disabled(graph, result)
    _compare_data_flow_internals(dft_graphs, package_cs_text, result, gate3_verdicts)

    return result
