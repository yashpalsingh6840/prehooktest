# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

Pre-code planning stage. Only two files exist today:

- [SSIS-Rewrite-Validation-Plan.md](SSIS-Rewrite-Validation-Plan.md) — the full design plan. Read
  it in full before doing any work here; this CLAUDE.md summarizes it but the plan is authoritative.
- [known_client_side_component_types.txt](known_client_side_component_types.txt) — a partially
  usable reference (see "Independence rule" below for what parts are off-limits).

The `ssis_validator/`, `tests/`, and `docs/` layout described in the plan (§6) does not exist yet.
Before writing any extraction code, the plan requires reading real sample `.dtsx`/`.cs` files
first (§7) — do not scaffold extraction logic from assumptions.

## What this tool is

An independent validator that checks whether a C# rewrite generated from an SSIS `.dtsx` package
actually reflects what that package does, without trusting the generation tool that produced the
rewrite. The generation tool is an untrusted black box.

## The independence rule (must not be violated)

This is the core constraint on every change in this repo:

- Knowledge may come from exactly two sources: Microsoft's public `.dtsx` XML / SSIS-to-.NET type
  documentation, and direct empirical inspection of real `.dtsx` and generated `.cs` files.
- Never open, search for, or reason about the source code, templates, or internal docs of the
  tool that generated the C#. If a file looks like it belongs to that tool's own implementation,
  it is out of bounds.
- `known_client_side_component_types.txt` is a mixed file: the plain list of SSIS component/task
  **type names** is usable (it's equivalent to consulting Microsoft's component catalog, and
  confirms that a `.dtsx` may carry either a friendly name like `"OLE DB Source"` or the canonical
  `componentClassID` like `Microsoft.OLEDBSource` — both must resolve to the same handler). The
  `[OK]/[PARTIAL]/[SEAM]/[GAP]` annotations and behavioral notes in that same file describe the
  *generation tool's own internal implementation limits* and must never be used — encoding them
  into the validator would make it tolerate the generator's known gaps instead of judging
  correctness independently, defeating the tool's purpose.

## Design principle: generic, not package-specific

The sample `.dtsx`/C# pairs used for development are synthetic, not real production packages.
They exist only to learn the empirical shape of the formats and to prove the validator's gates
work — they must never become the spec:

- No hardcoded table/column/task/component/connection-manager/variable/package names or file
  paths anywhere in extraction, comparison, or gate logic.
- Extraction keys off structural markers (documented XML element/attribute shapes on the SSIS
  side; C# structural conventions confirmed across *multiple* real generated files, never copied
  from one file) — a single sample file is a data point, not a hypothesis.
- Anything the tool doesn't recognize (an SSIS component type, a SQL source shape, a generated
  code shape) must degrade explicitly: a reported `Unrecognized`/`Unmatched`/`Unverifiable`
  finding with available evidence — never a silent pass, never a crash.
- Coverage is an extensible registry (SSIS `CreationName` → extractor function on the left,
  confirmed generated-code shape → extractor function on the right). Add entries for
  types/shapes actually observed in docs or real samples; never pre-build entries for unobserved
  SSIS features.

## The three gates (all static — no execution, ever)

No gate runs the SSIS package, runs the generated program, or connects to a database; everything
comes from reading `.dtsx` XML and generated `.cs` as text/structure.

1. **Gate 1 — SQL statement & schema parity**: every SQL statement and table/column schema the
   package declares (Execute SQL Task, OLE DB/ADO.NET source/destination query, Lookup reference
   query, OLE DB Command) vs. its counterpart in the generated C#, compared structurally via a
   real SQL parser (`sqlglot`), not raw text diff.
2. **Gate 2 — control-flow / data-flow graph parity**: left graph from `.dtsx` (nodes =
   tasks/containers/components, edges = precedence constraints/paths, plus concurrency groups
   computed from scratch); right graph from generated C# (nodes = generated units of work, edges =
   call order/parallel constructs). Compare node sets, edge sets, concurrency groupings.
3. **Gate 3 — per-component input/output schema parity**: declared column name+type shape per
   SSIS data-flow component vs. declared shape in the generated unit, using Microsoft's
   SSIS-type-to-.NET-type mapping (verified from docs, never assumed).

Working order per the plan (§7): build and prove Gate 1 fully (including a deliberate-mismatch
proof on a mutated copy — never edit an original fixture) before starting Gate 2; same for Gate 2
before Gate 3.

## Tech stack (from the plan, §5)

- Python 3.11+, deliberately different from the generated output's language.
- XML: `xml.etree.ElementTree` first; escalate to `lxml` only if real nesting/namespace handling
  needs XPath features ElementTree lacks.
- SQL comparison: `sqlglot`, comparing parsed structure, not raw text.
- C# parsing: start with regex/text extraction validated against multiple real generated files;
  escalate to `tree-sitter-c-sharp` only if the real shape proves too irregular — decide this
  empirically.
- Reports: JSON (machine-readable) + Markdown (human-readable), always evidence-bearing, verdicts
  from `Match` / `NormalizedMatch` / `Mismatch` / `LeftOnly` / `RightOnly` / `Unrecognized` /
  `Unverifiable` — never a bare pass/fail.
- CLI (once built): `ssis_validator validate --dtsx <path> --generated <dir> --out <dir>`, one
  `.dtsx` + one generated-folder pair per run (batch mode is a deferred extension).

## Planned layout (§6 of the plan)

```
ssis_validator/
  dtsx/       Left-side extraction from .dtsx XML (control_flow.py, data_flow.py,
              sql_extraction.py, schema_types.py)
  codegen/    Right-side extraction from generated .cs (csharp_sql.py, csharp_graph.py,
              csharp_schema.py)
  compare/    gate1_sql.py, gate2_graph.py, gate3_schema.py
  report/     json_report.py, markdown_report.py
  cli.py
tests/
  fixtures/   Sample .dtsx + generated .cs pairs, plus deliberately-mutated copies for
              negative-case tests — never edit an original fixture in place.
docs/
  dtsx-xml-notes.md   Notes from reading MS docs + real files — kept generic, no
                       sample-specific values baked in as "the schema."
```

## Working discipline (§7 of the plan)

1. Read real `.dtsx`/`.cs` samples before writing any extraction code — no guessed schema.
2. Prove each gate can fail before trusting it can pass: after a gate reports a clean match,
   create a mutated *copy* of one side with one deliberate change and confirm the gate reports
   the specific mismatch with evidence.
3. Every SQL statement, graph node/edge, and component I/O shape must be accounted for on both
   sides with exact counts and evidence — never a vague summary, never a silent skip.
4. "Clean on the samples" means the mechanism works, not that coverage is complete (samples are
   synthetic, not the full real-world surface).
