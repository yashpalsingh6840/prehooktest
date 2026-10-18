"""Right-side extraction: control-flow call graph from generated C#.

Structural conventions confirmed across the generated project's top-level orchestration file —
see docs/dtsx-graph-notes.md §4. Regex/text extraction with explicit brace-matching for method
bodies (a real nested-brace grammar; a single non-greedy regex cannot reliably bound it).
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

_METHOD_HEADER_RE = re.compile(
    r"(?:public|internal)\s+(?:sealed\s+)?(?:async\s+)?Task(?:<\w+>)?\s+(\w+)\s*\("
    r"(?:IUnitOfWork\s+\w+\s*,\s*)?CancellationToken\s+\w+\s*\)\s*\{"
)


@dataclass
class CsharpControlFlowGraph:
    file_path: str
    sequential_children: dict[str, list[str]] = field(default_factory=dict)
    concurrency_groups: list[list[str]] = field(default_factory=list)
    failure_handler_names: set[str] = field(default_factory=set)
    foreach_absorbed_children: dict[str, str] = field(default_factory=dict)
    guarded_nodes: dict[str, str] = field(default_factory=dict)
    all_called_names: set[str] = field(default_factory=set)


def _extract_method_bodies(text: str) -> dict[str, str]:
    """Finds every `... Task[<T>] Name(... CancellationToken ct) { ... }` method and returns its
    brace-matched body text, keyed by name. A plain regex can't bound nested braces reliably, so
    this walks forward counting depth from each header match."""
    bodies: dict[str, str] = {}
    for m in _METHOD_HEADER_RE.finditer(text):
        name = m.group(1)
        depth = 1
        i = m.end()
        while i < len(text) and depth > 0:
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
            i += 1
        bodies[name] = text[m.end():i - 1]
    return bodies


_SEQUENTIAL_CALL_RE = re.compile(r"await\s+(\w+)\(uow,\s*\w+\);")


def _extract_sequential_calls(body: str) -> list[str]:
    return _SEQUENTIAL_CALL_RE.findall(body)


def _find_matching_paren(text: str, open_paren_index: int) -> int:
    depth = 1
    i = open_paren_index + 1
    while i < len(text) and depth > 0:
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
        i += 1
    return i - 1


_WHEN_ALL_START_RE = re.compile(r"Task\.WhenAll\(")
_BRANCH_CALL_RE = re.compile(r"await\s+(\w+)\(branchUow,\s*\w+\);")


def _extract_concurrency_groups(text: str) -> list[list[str]]:
    groups = []
    for m in _WHEN_ALL_START_RE.finditer(text):
        open_paren = m.end() - 1
        close_paren = _find_matching_paren(text, open_paren)
        block = text[open_paren + 1:close_paren]
        members = _BRANCH_CALL_RE.findall(block)
        if members:
            groups.append(members)
    return groups


_HANDLERS_RUN_ADD_RE = re.compile(r'handlersRun\.Add\(\s*"([^"]+)"\s*\)')


def _extract_failure_handler_names(text: str) -> set[str]:
    return set(_HANDLERS_RUN_ADD_RE.findall(text))


_FOREACH_LOOP_STEP_RE = re.compile(r"new\s+ForEachLoopStep\(")
_STATEMENT_REF_RE = re.compile(r"(\w+)Statement\.BuildStatement")


def _extract_foreach_absorbed_child(body: str) -> str | None:
    if not _FOREACH_LOOP_STEP_RE.search(body):
        return None
    m = _STATEMENT_REF_RE.search(body)
    return m.group(1) if m else None


_GUARD_COMMENT_RE = re.compile(
    r"//\s*Guard:\s*(?P<expr>.+?)\n\s*if\s*\(.*?await\s+(?P<name>\w+)\(uow,\s*\w+\);",
    re.DOTALL,
)


def _extract_guarded_nodes(text: str) -> dict[str, str]:
    return {m.group("name"): m.group("expr").strip() for m in _GUARD_COMMENT_RE.finditer(text)}


def extract(package_cs_file: str | Path) -> CsharpControlFlowGraph:
    path = Path(package_cs_file)
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    graph = CsharpControlFlowGraph(file_path=str(path))

    bodies = _extract_method_bodies(text)
    for name, body in bodies.items():
        sequential = _extract_sequential_calls(body)
        if sequential:
            graph.sequential_children[name] = sequential
            graph.all_called_names.update(sequential)
            continue
        absorbed = _extract_foreach_absorbed_child(body)
        if absorbed is not None:
            graph.foreach_absorbed_children[name] = absorbed
            graph.all_called_names.add(absorbed)

    graph.concurrency_groups = _extract_concurrency_groups(text)
    for group in graph.concurrency_groups:
        graph.all_called_names.update(group)

    graph.failure_handler_names = _extract_failure_handler_names(text)
    graph.all_called_names.update(graph.failure_handler_names)

    graph.guarded_nodes = _extract_guarded_nodes(text)

    return graph


def find_package_cs_file(generated_package_dir: str | Path) -> Path | None:
    """The top-level orchestration file is named `<PackageName>.cs`, directly under the package
    dir (not in a subfolder — Fills/, ScriptTasks/, Mapping/, etc. all sit alongside it).
    Falls back to identifying it by content (a RunAsync method) when the directory itself has
    been renamed/copied (e.g. into a test's tmp_path), since `Program.cs` also lives at the top
    level and a bare "*.cs" glob can't tell them apart by name alone."""
    root = Path(generated_package_dir)
    candidate = root / f"{root.name}.cs"
    if candidate.exists():
        return candidate
    candidates = [p for p in root.glob("*.cs") if p.name != "Program.cs"]
    if len(candidates) == 1:
        return candidates[0]
    for p in candidates:
        if "RunAsync" in p.read_text(encoding="utf-8-sig", errors="replace"):
            return p
    return None
