"""Right-side lookup: additional generated source referenced by a DFT/control-flow orchestration
excerpt but not contained within it.

Confirmed empirically (RegionSummaryTransform, AGG_ByRegion, MRG_CustomerContacts,
SCR_NotifyAndLogProgressScriptTask — Package_Transforms and Package, 2026-09-12, discovered while
hand-running docs/ai-validation-prompt.md against real packets): the DFT orchestration method (or
Script Task wiring class) that codegen/csharp_graph.py's `_extract_method_bodies` captures only
wires together named factory calls (`FF_SRC_Customers()`, `new RegionSummaryTransform()`,
`new SCR_NotifyAndLogProgressScriptTask(...)`) — the real per-row/per-script business logic lives
in one of:

- A sibling factory method in the SAME orchestration file, of a different signature that
  `_extract_method_bodies`'s `Task<T> Name(..., CancellationToken ct)`-only regex does not match
  — e.g. `internal IRowSource<T> AGG_ByRegion()`, which itself contains nested local functions
  (`BuildAggregateSource`, `BuildRawSource`) carrying the actual join/filter/aggregate logic.
- `Mapping/<ClassName>.cs` — a small, single-purpose `IRowTransform<TIn,TOut>` class.
- `Fills/<ClassName>.Fills.cs` — a "ssisx-fill" partial-method implementation: the ported
  business logic for a Script Component's per-row fill, or a Script Task's entire body.
- `ScriptTasks/<ClassName>.cs` — the thin wiring class whose real logic sits in the paired Fills
  file above.

These are the generated deliverable's own files — in-scope per the independence rule (never
gaps/, extract/, svk/, coverage-report.md, or a generated project's own README.md). Found by
textually scanning an excerpt for referenced identifiers (`new SomeType(` or a bare `SomeName(`
call) and resolving each one against this SAME package directory only. An identifier that
resolves to nothing (an Etl.Core framework type/helper used via `new` or a DI call — SqlBulkSink,
CsvRowSource, DataFlowStep, Log, Opt, etc. — none of which have a defining file inside the
package's own directory) is silently skipped, not an error: it's shared runtime behavior, not
this component's own logic.
"""
from __future__ import annotations

import re
from pathlib import Path

_SKIP_DIR_NAMES = {"bin", "obj", "TestResults"}

# A negative lookbehind for "." excludes a member-access call on an instance/object (e.g. the
# generic `step.RunAsync(...)` every Etl.Core step type exposes, or `ctx.Uow.ExecuteSqlAsync(...)`)
# -- those are calls on a value already in scope, not a reference to a sibling factory method or
# class this package itself defines, and chasing them produces confusing, unrelated matches (a
# same-named method purely by coincidence, e.g. the package's own top-level `RunAsync`).
_REFERENCED_IDENTIFIER_RE = re.compile(r"(?<!\.)\b([A-Z]\w*)\s*(?:<[^<>]*>)?\s*\(")

_CLASS_DECL_RE_TEMPLATE = r"\bclass\s+{name}\b"

# Any method/local-function header, with or without access modifiers (a C# local function -- as
# used for AGG_ByRegion()'s nested BuildAggregateSource()/BuildRawSource() -- carries none),
# brace-matched the same way csharp_graph.py's own `_extract_method_bodies` handles nested braces.
_ANY_METHOD_HEADER_RE = re.compile(
    r"(?:(?:public|internal|private|static|async|sealed)\s+)*"
    r"[\w][\w.\[\]]*(?:<[^<>]*>)?\??\s+(\w+)\s*\([^;{}]*\)\s*\{"
)


def referenced_identifiers(excerpt: str) -> list[str]:
    """Every capitalized `Identifier(` / `new Identifier(` reference in an excerpt, first-seen
    order, deduplicated. Deliberately broad (no allow/deny list) -- resolution against real
    project files is what filters out framework noise, not the pattern itself."""
    seen: list[str] = []
    for name in _REFERENCED_IDENTIFIER_RE.findall(excerpt):
        if name not in seen:
            seen.append(name)
    return seen


def extract_named_method_bodies(text: str) -> dict[str, str]:
    bodies: dict[str, str] = {}
    for m in _ANY_METHOD_HEADER_RE.finditer(text):
        name = m.group(1)
        depth = 1
        i = m.end()
        while i < len(text) and depth > 0:
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
            i += 1
        if name not in bodies:  # first definition wins (matches _extract_method_bodies' own rule)
            bodies[name] = text[m.end():i - 1]
    return bodies


def _iter_cs_files(generated_package_dir: str | Path):
    root = Path(generated_package_dir)
    for cs_file in root.rglob("*.cs"):
        if any(part in _SKIP_DIR_NAMES for part in cs_file.parts):
            continue
        yield cs_file


def find_class_source(generated_package_dir: str | Path, class_name: str) -> list[tuple[str, str]]:
    """[(relative_file_path, full_file_text)] for every file in this package's own directory
    declaring `class <class_name>` -- a class can legitimately be split across a Mapping/ file
    and a Fills/ partial, so every match is returned, not just the first."""
    root = Path(generated_package_dir)
    pattern = re.compile(_CLASS_DECL_RE_TEMPLATE.format(name=re.escape(class_name)))
    results = []
    for cs_file in _iter_cs_files(generated_package_dir):
        text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
        if pattern.search(text):
            results.append((str(cs_file.relative_to(root)), text))
    return results


def find_name_mentions(
    generated_package_dir: str | Path, name: str, max_matches: int = 5, context_lines: int = 6,
) -> list[dict[str, str]]:
    """Bounded fallback for a component with NO known correlate anywhere (not even Gate 1's own
    SQL-content correlation) -- a plain, capped text search for the component's own name as a
    literal substring across this package's own generated files, returning a small excerpt (not
    the whole file) around each hit. Deliberately capped at `max_matches`: this is meant to hand
    an AI reviewer a short, plausible candidate list to judge, never an unbounded project-wide
    dump. A component search that turns up zero mentions is itself real, reportable evidence (the
    generated project may genuinely not reference this component's name anywhere) -- not an
    error."""
    results: list[dict[str, str]] = []
    root = Path(generated_package_dir)
    for cs_file in _iter_cs_files(generated_package_dir):
        if len(results) >= max_matches:
            break
        text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
        idx = text.find(name)
        if idx == -1:
            continue
        line_start = text.rfind("\n", 0, idx)
        lines_before = text[:line_start].count("\n") if line_start != -1 else 0
        all_lines = text.splitlines()
        line_no = min(lines_before, len(all_lines) - 1)
        lo = max(0, line_no - context_lines)
        hi = min(len(all_lines), line_no + context_lines + 1)
        results.append({
            "file": str(cs_file.relative_to(root)),
            "excerpt": "\n".join(all_lines[lo:hi]),
        })
    return results


def collect_related_sources(
    generated_package_dir: str | Path,
    excerpt: str | None,
    own_file_text: str | None = None,
    own_file_path: str | None = None,
    component_name: str | None = None,
) -> dict[str, list[dict[str, str]]]:
    """For every referenced identifier in `excerpt`, PLUS `component_name` itself, returns
    {identifier: [{"file": relative_path, "source": text}, ...]} for whichever ones resolve to
    real generated source in this package's own directory -- first checking for a sibling
    same-file factory method (own_file_text), then a separate class file. An identifier that
    resolves to neither (an Etl.Core framework construct) is simply absent from the result.

    `component_name` closes a real gap found by direct inspection (Package_Transforms'
    DER_Enrich, 2026-09-12): a component's own per-row logic is filed under a class named
    *exactly* after the SSIS component itself (`Mapping/DER_Enrich.cs`), even when nothing in
    this specific DFT's orchestration excerpt calls it directly by that name -- it's instead
    referenced two levels removed, from inside a *different* component's own Mapping/ class
    (here, `CSPLIT_Validity_Valid.Map` calls `DER_Enrich.FullName(...)`). Excerpt-identifier
    scanning alone is one level deep and would miss this; checking the component's own name
    directly does not depend on how many levels of indirection separate it from the excerpt."""
    if not excerpt and not component_name:
        return {}

    same_file_methods = extract_named_method_bodies(own_file_text) if own_file_text else {}
    related: dict[str, list[dict[str, str]]] = {}

    names = list(referenced_identifiers(excerpt)) if excerpt else []
    if component_name and component_name not in names:
        names.append(component_name)

    for name in names:
        entries: list[dict[str, str]] = []
        body = same_file_methods.get(name)
        if body is not None and body.strip() and (not excerpt or body.strip() != excerpt.strip()):
            entries.append({"file": f"{own_file_path} (method {name})", "source": body})
        for file_path, text in find_class_source(generated_package_dir, name):
            entries.append({"file": file_path, "source": text})
        if entries:
            related[name] = entries

    return related
