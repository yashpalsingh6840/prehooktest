# Independent SSIS-to-Rewrite Validation Tool — Plan

## 1. Purpose

Build a validator that checks whether a C# rewrite generated from an SSIS `.dtsx` package
actually reflects what that package does — **without trusting the generation tool's own account
of itself**. The generation tool is treated as an untrusted black box: we never look at its
source, its templates, or any documentation of how it works.

This tool must never converge with the generator's own assumptions. If it did, a bug in the
generator could produce output that a validator sharing its assumptions would agree with — which
defeats the purpose. Independence is the deliverable, more than the polish of any one gate.

## 2. Independence rule

Everything the tool knows comes from exactly two sources:

1. **Microsoft's public documentation of the `.dtsx` XML format** (and the SSIS→.NET data type
   mapping) — read and re-verified as needed, never assumed.
2. **Direct, empirical inspection of real `.dtsx` files and real generated `.cs` files** — read
   before writing any extraction code, so the extraction logic reflects what the files actually
   contain, not a guessed schema.

We do not open, search for, or reason about the source code, templates, extraction logic, or
internal documentation of the tool that produced the C#. If a file looks like it belongs to that
tool's own implementation, it's out of bounds.

## 3. Design principle: generic, not package-specific

**This is the most important addition to the plan and constrains every line of code we write.**

Context: the `.dtsx` packages available for this build are not real production packages (those
are on the client side and inaccessible) — they are synthetic packages built to resemble the
client's real packages, generated once against the existing code-gen tool to produce a sample
rewrite. These samples exist **only** to let us:

- learn the real, empirical shape of `.dtsx` XML and generated C# (structure, not values), and
- prove the validator's gates can correctly report both match and mismatch.

They must never become the spec. Concretely:

- **No hardcoded table names, column names, task/component names, connection manager names,
  variable names, package names, or file paths** anywhere in extraction, comparison, or gate
  logic. Anything that looks like it references a specific sample package by name is a bug.
- Extraction logic keys off **structural markers** — XML element/attribute names and shapes
  documented by Microsoft (e.g. the executable/container hierarchy, `PrecedenceConstraint`
  shape, task-specific property bags, pipeline component/path shape, external metadata column
  shape) and off **C# structural conventions confirmed across multiple real generated files**
  (e.g. "SQL literals live in small dedicated classes with a consistent method shape" — a pattern
  we confirm by reading several examples, not a rule copied from one file).
  A single sample file is a data point, not a hypothesis.
- Where SSIS has more task/component types or C# shapes than any one sample package exercises,
  the tool must **degrade explicitly, not silently**: an unrecognized SSIS component type, an
  unrecognized SQL source shape, or a generated-code shape the parser doesn't understand becomes
  a reported finding — `Unrecognized` / `Unmatched` / `Unverifiable` with whatever evidence is
  available — never a silent pass and never a crash.
- Coverage grows as an **extensible registry**, not a rewrite: a mapping from SSIS `CreationName`
  → extractor function on the Left, and a mapping from confirmed generated-code shape → extractor
  function on the Right. Adding a new task/component type later means adding an entry, not
  restructuring the tool. We do not pre-build entries for SSIS features we haven't observed in
  either the docs or a real sample — that would be guessing a schema, which §2 already rules out.
- The proof step (§7) — deliberately mutating a copy of one side to confirm a gate reports the
  right mismatch — validates the *mechanism*, not the sample data. Passing on the sample packages
  is a necessary checkpoint, never treated as "done for all SSIS packages."

## 4. The three gates (all static — no execution, ever)

No gate runs the SSIS package, runs the generated program, or connects to a database. Everything
comes from reading the `.dtsx` XML and the generated `.cs` source as text/structure.

### Gate 1 — SQL statement & schema parity
For every SQL statement the package declares (Execute SQL Task, OLE DB/ADO.NET source or
destination query, Lookup reference query, OLE DB Command parameterized statement, etc.) and
every table/column schema it declares reading or writing:
- Extract the literal SQL text and associated table/column names + types from the `.dtsx` XML.
- Find the corresponding SQL text/schema in the generated C# (shape TBD empirically — literal
  string, small helper method/class, entity class properties, etc.).
- Compare structurally (tables touched, read vs. written, columns/types, SQL semantics via a real
  SQL parser) — tolerant of formatting differences, not a raw text diff.
- Every item from both sides is reported, matched or not; an unmatched item is a finding.

### Gate 2 — control-flow / data-flow graph parity
- **Left graph**, from the `.dtsx` XML: nodes = tasks/containers/data-flow components; edges =
  precedence constraints (control flow) and paths (data flow); plus which nodes have no ordering
  constraint between them (concurrency groups), computed from scratch each time.
- **Right graph**, from the generated C#: nodes = whatever units of work the generator emits;
  edges = the order/structure in which the code invokes them (sequential calls, explicit
  parallel-execution constructs).
- Compare node sets, edge sets, and concurrency groupings; every mismatch reported with both
  sides' evidence.

### Gate 3 — per-component input/output schema parity
Declared shape only — never actual data values or execution:
- Per SSIS data-flow component: declared input column list (name + SSIS type) and output column
  list (name + SSIS type) from the XML.
- Per corresponding generated unit: declared input/output shape from the C# (parameter types,
  row/entity class properties, etc.).
- Compare names and types, mapping SSIS types (`DT_WSTR`, `DT_I4`, `DT_R8`, …) to .NET types via
  Microsoft's published mapping (verified against source docs, not assumed).
- A component that can't be confidently matched to a generated counterpart is a reported
  `Unmatched` finding, not a silent pass.

## 5. Tech stack

- **Python** — a deliberately different language/toolchain from the generated output, so nothing
  is structurally shared with the generator.
- XML parsing: `xml.etree.ElementTree` to start; move to `lxml` only if real `.dtsx` nesting or
  namespace handling needs XPath features ElementTree lacks.
- SQL comparison: a real SQL parser (`sqlglot`) so formatting differences don't create false
  mismatches — diff parsed structure (tables/columns/types), not raw text.
- C# parsing: start with careful, well-tested text/regex extraction validated against multiple
  real generated files. Escalate to a grammar-based parser (`tree-sitter-c-sharp`) only if the
  real shape proves too irregular for regex — decide this empirically, not up front.
- Reports: JSON (machine-readable) + Markdown (human-readable) per gate, always evidence-bearing.

- Python target: 3.11+ (no constraint on this machine).
- CLI: one `.dtsx` + one generated-folder pair per invocation to start (`ssis_validator
  validate --dtsx <path> --generated <dir> --out <dir>`). Batch mode (multiple pairs in one run)
  is a straightforward later extension once the single-pair path is proven — not built now.

## 6. Layout

```
D:\PoC\SSIS_RewriteValidationTool\
  SSIS-Rewrite-Validation-Plan.md      <- this file
  ssis_validator/
    dtsx/                              Left-side extraction (from .dtsx XML)
      control_flow.py
      data_flow.py
      sql_extraction.py
      schema_types.py                 SSIS type -> .NET type mapping (from MS docs)
    codegen/                          Right-side extraction (from generated .cs)
      csharp_sql.py
      csharp_graph.py
      csharp_schema.py
    compare/
      gate1_sql.py
      gate2_graph.py
      gate3_schema.py
    report/
      json_report.py
      markdown_report.py
    cli.py
  tests/
    fixtures/                         Sample .dtsx + generated .cs pairs (as provided),
                                       plus deliberately-mutated copies for negative-case tests.
                                       Never edit an original fixture in place.
    test_gate1.py
    test_gate2.py
    test_gate3.py
  docs/
    dtsx-xml-notes.md                 What we learn reading MS docs + real files — kept generic,
                                       no sample-specific values baked in as "the schema."
  README.md
```

## 7. Working discipline

1. **Read before coding.** Before writing any extraction code, spend real time reading the actual
   `.dtsx` XML and generated `.cs` samples to learn their true shape. No guessed schema.
2. **Build and prove Gate 1 first**, on one real (sample) package pair, before starting Gate 2.
   Same for Gate 2 before Gate 3.
3. **Prove each gate can fail before trusting it can pass.** Once a gate reports a clean match on
   a sample package, create a modified *copy* of one side (never edit the original) with one
   deliberate change — a different SQL literal, a renamed column, a removed precedence
   constraint — and confirm the gate reports the mismatch with correct, specific evidence. Only
   trust a "match" verdict once "mismatch" has been proven to work too. Do this for every gate.
4. **Exact counts and evidence always** — never a vague summary, never a silent skip. Every SQL
   statement, every graph node/edge, every component I/O shape is accounted for on both sides.
5. Because the samples aren't real production packages, treat "clean on the samples" as "the
   mechanism works," not "coverage is complete" — see §3.

## 8. Reporting

`ssis_validator validate --dtsx <path(s)> --generated <dir(s)> --out <dir>` runs all three gates
and produces one JSON + one Markdown report, listing every SQL item / graph node-edge / component
I/O shape from both sides with a verdict (`Match` / `NormalizedMatch` / `Mismatch` / `LeftOnly` /
`RightOnly` / `Unrecognized` / `Unverifiable`) and the evidence behind it. Never a bare pass/fail.

## 9. Decisions confirmed with the user (2026-09-08)

1. **Dependencies:** pip installs allowed — `sqlglot` for SQL structural comparison, `lxml` if
   ElementTree proves insufficient.
2. **Invocation:** one `.dtsx` + one generated-folder pair per run to start; batch mode deferred.
3. **Python version:** free to target 3.11+, no machine constraint.

## 10. Known real-world component/task types (coverage prioritization only)

The user supplied `known_client_side_component_types.txt`, a scan of the real client SSIS
portfolio. That file mixes two things, and only one of them is usable here without crossing the
independence boundary in §2:

- **Usable:** the plain list of SSIS component/task type names actually seen in the wild. This is
  no different from consulting Microsoft's own component catalog — it just tells us which types
  to prioritize when building out the extensible registry (§3), and confirms a real `.dtsx`
  quirk worth handling: an unrevalidated package can carry an older SSDT designer's **friendly
  name** (`"Flat File Source"`, `"OLE DB Source"`, etc.) instead of the canonical
  `componentClassID` (`Microsoft.FlatFileSource`, `Microsoft.OLEDBSource`) for a component never
  re-saved in a newer designer — both spellings must resolve to the same handler.
- **Not usable:** that file's `[OK]/[PARTIAL]/[SEAM]/[GAP]` annotations and behavioral notes
  describing what the code-gen tool supports per type (e.g. "Aggregate only GroupBy+Count,"
  "MergeJoin only exactly two sources," verified by "reading the emitter code directly"). That is
  a description of the generation tool's own internal implementation and limitations — using it
  would make the validator expect/tolerate exactly the generator's known gaps instead of
  independently judging correctness, which is the one thing this tool must never do. Discarded
  entirely, per the user's confirmation.

Real-world type names to prioritize (names only, no behavior assumptions attached):

*Pipeline components:* `Microsoft.OLEDBSource`, `Microsoft.OLEDBDestination`,
`Microsoft.FlatFileSource`, `Microsoft.FlatFileDestination`, `Microsoft.ADONETDestination`,
`Microsoft.DataReaderSourceAdapter` (ADO.NET Source), `Microsoft.ExcelSource`,
`Microsoft.DerivedColumn`, `Microsoft.DataConvert`, `Microsoft.ConditionalSplit`,
`Microsoft.Multicast`, `Microsoft.Lookup`, `Microsoft.Aggregate`, `Microsoft.Sort`,
`Microsoft.Merge`, `Microsoft.MergeJoin`, `Microsoft.UnionAll`, `Microsoft.RowCount`,
`Microsoft.OLEDBCommand`, `Microsoft.ScriptComponentHost` (Script Component).

*Control-flow tasks/containers:* `Microsoft.ExecuteSQLTask`, `Microsoft.FileSystemTask`,
`Microsoft.ScriptTask`, `Microsoft.Package` (root), `Microsoft.Pipeline` (Data Flow Task
container). `Microsoft.ExecutePackageTask` was not observed in that portfolio scan; `Sequence
Container` and `ForEach Loop Container` were reported as unconfirmed either way by that scan —
both are standard, well-documented SSIS control-flow constructs regardless, so the registry
covers them from Microsoft's docs directly rather than waiting on portfolio confirmation.

This list only orders *which registry entries to build and test first*. It never becomes a
ceiling: any `.dtsx` this tool is later pointed at may contain a type not in this list, and per
§3 that must produce an explicit `Unrecognized` finding, never a crash or a silent pass.

## 11. Next step

Waiting on: path(s) to real (sample) `.dtsx` file(s) and the corresponding generated C# folder
path(s). Once provided, work starts with the read-only inspection step in §7 — no extraction code
until the real shapes have been read.
