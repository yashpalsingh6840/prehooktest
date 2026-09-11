"""SSIS component type -> the Etl.Core runtime construct(s) that would represent it if
translated.

`Etl.Core` is the shared runtime library shipped alongside every generated package (referenced
by each generated project's own `.csproj`) — it is part of the delivered rewrite, not the
code-generation tool's own separate internals, so reading its class names is in-scope under the
independence rule (confirmed with the user 2026-09-12, alongside the rest of this check's design
— see docs/Gate-Report-Findings-2026-09-09.md-style write-ups for the precedent of confirming a
boundary call before acting on it).

Confirmed empirically, by direct inspection of all 5 real generated packages: these construct
names exist in `Etl.Core` but are referenced by none of the 5 sample projects for the paired
SSIS component type below — `SortingRowSource` (Sort), `ConcatenatingRowSource` (Union All),
`MergeInterleaveRowSource` (Merge — the "already sorted" merge, distinct from Merge Join, whose
own `MergeJoinRowSource` construct IS used in the samples), and `CountingRowSource` (Row Count).
Their complete absence from an entire generated project is a real, checkable signal that the
component's semantics were dropped — not a naming mismatch this tool can't resolve, and not
proof either (the generator could implement the same semantic without this specific wrapper
class; see the coarse, project-wide, presence-only nature of this check, documented in
codegen/csharp_construct_presence.py and compare/gate3_schema.py).

Extend this registry only for a componentClassID confirmed via this same method (observe the
candidate construct(s) exist in Etl.Core; confirm real generated samples never reference them
for that SSIS type) — never pre-guess an unobserved SSIS type's expected translation shape.
"""
from __future__ import annotations

NO_TRANSLATION_CONSTRUCTS: dict[str, tuple[str, ...]] = {
    "Microsoft.Sort": ("SortingRowSource",),
    "Microsoft.UnionAll": ("ConcatenatingRowSource",),
    "Microsoft.Merge": ("MergeInterleaveRowSource",),
    "Microsoft.RowCount": ("CountingRowSource",),
}
