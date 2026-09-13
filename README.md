# SSIS Rewrite Validation Tool

Independent, static validator that checks whether a C# rewrite generated from an SSIS `.dtsx`
package actually reflects what that package does — without trusting the generation tool that
produced the rewrite. See [SSIS-Rewrite-Validation-Plan.md](SSIS-Rewrite-Validation-Plan.md) for
the full design and the independence rule every line of this tool must respect, and
[docs/dtsx-xml-notes.md](docs/dtsx-xml-notes.md) / [docs/dtsx-graph-notes.md](docs/dtsx-graph-notes.md)
for the empirical XML/C# shapes the extraction logic is built from.

Nothing here ever executes the SSIS package or the generated program, or connects to a database.
Everything comes from reading `.dtsx` XML and generated `.cs` as text/structure, and every result
carries a verdict plus evidence — never a bare pass/fail.

## The big picture: two tiers

```
Tier 1 — Gates 1, 2, 3            Tier 2 — AI-assisted adjudication (optional, advisory)
────────────────────────          ──────────────────────────────────────────────────────
Fully deterministic.              Only for the small slice of findings Tier 1 honestly
No AI involved, ever.             cannot resolve on its own — a business-logic-equivalence
Rule-based comparison of          judgment ("does this generated code do the same thing as
.dtsx XML vs. generated C#.       this expression?"), not a structural comparison.

Verdicts: Match / NormalizedMatch /   Verdicts: AiConfirmedMatch / AiSuspectedDiscrepancy /
Mismatch / LeftOnly / RightOnly /     AiInconclusive / AiNotApplicable — never merged into
Unrecognized / Unverifiable           Tier 1's reports, never authoritative on its own.
```

Roughly half of everything the three gates check ends up `Unverifiable` — not a failure, an
honest "a structural diff can't answer this." Tier 2 exists only to take a second, AI-assisted
pass over the genuinely irreducible portion of that pile (not all of it — see
[docs/ai-validation-prompt.md](docs/ai-validation-prompt.md)), and its output is always a
separate, human-reviewed file that never edits Tier 1's own reports.

## Status

**All three Tier 1 gates are implemented and proven** against all 5 sample packages, and Tier 2
(evidence packaging + AI adjudication) is built and has been run for real against two of them.

- **Gate 1** — SQL statement & schema parity. Every SQL statement and table/column schema
  declared on the SSIS side vs. its generated counterpart, compared via a real SQL parser
  (`sqlglot`), not text diff.
- **Gate 2** — control-flow/data-flow graph parity. Compares the `.dtsx`'s task/container graph
  (nodes, precedence-constraint edges, concurrency groups) against the generated code's own call
  structure. Its data-flow-*internal* (per-component, inside one Data Flow Task) check is a plain
  name-presence signal on its own — see [docs/dtsx-graph-notes.md](docs/dtsx-graph-notes.md) §6
  for why a stronger claim isn't safe at that level — but it now defers to Gate 3's own verdict
  for the same component when Gate 3 already ran a real schema comparison, rather than
  re-reporting the weaker signal; a component Gate 3 doesn't cover still falls back to the
  presence-only check.
- **Gate 3** — per-component input/output schema parity. Correlates a destination by (schema,
  table) identity (reusing Gate 1's proven match), a source by its generated row-source factory
  method name, and — since Phase A — a non-database destination (e.g. `FlatFileDestination`) by
  an enclosing factory method named after the component, plus a project-wide presence/absence
  check for SSIS component types (Sort, Union All, Merge, Row Count) that have no generated
  counterpart at all anywhere in the project. Compares column names and SSIS-type-to-.NET-type
  mapping (verified against Microsoft's published mapping — `ssis_validator/dtsx/schema_types.py`).
  A pipeline-internal transform with no declared external shape to compare (DerivedColumn,
  ConditionalSplit, Lookup, Aggregate, MergeJoin, Script Component) now carries structured
  semantic evidence on its `Unverifiable` finding (Phase B) — the readable expression, resolved
  lineage, decoded sort direction, or Script source — instead of a bare "no shape to compare"
  message, so a human or Tier 2 can act on it without re-reading the raw `.dtsx`.

Net effect of Phase A + B on the 5 sample packages: 10 of 82 originally-`Unverifiable` Gate 3
findings resolved into real, tool-solvable verdicts (8 `LeftOnly`, 2 `Match`); every remaining
`Unverifiable` finding that has a registered semantic extractor now carries real evidence.

## Setup

```bash
python -m venv .venv
.venv\Scripts\pip install -e ".[dev]"
```

## Usage — Tier 1 (the deterministic gates)

```bash
.venv\Scripts\python -m ssis_validator.cli validate --dtsx <path-to.dtsx> --generated <generated-project-dir> --out <report-dir>
```

Writes `<package>.gate{1,2,3}.{json,md}` to `--out`. Exit code is non-zero if any Gate 1 finding
is `Mismatch`/`LeftOnly`/`RightOnly`/`Unrecognized`, any Gate 2 finding is
`Mismatch`/`LeftOnly`/`RightOnly`, or any Gate 3 finding is `Mismatch`/`LeftOnly`.

Before rerunning, clean the report directory's top-level files first (never delete a
user-created archive subfolder, e.g. `gate_reports/V1/`) so old and new runs are never mixed.

## Usage — Tier 2 (AI-assisted adjudication)

Tier 2 is two separate steps, deliberately: a mechanical one (no AI, just extraction) and a
judgment one (AI, tightly scoped).

**Step 1 — emit the evidence packet** (deterministic, no AI):

```bash
.venv\Scripts\python -m ssis_validator.cli ai-context --dtsx <path-to.dtsx> --generated <generated-project-dir> --out ai_context/<PackageName> --combined
```

`--combined` (recommended) writes one `<package>.ai-context.md` for the whole package: every
generated source file the evidence resolves to is listed once (in a `## Sources` section) and
every component references it by path instead of re-embedding it — this both shrinks the token
footprint and means the whole package can be adjudicated in **one** AI conversation instead of one
per component. Omit `--combined` for the older one-file-per-component layout if you specifically
want to hand a single component to an AI in isolation.

**Step 2 — hand the packet to an AI and get a verdict.** The rules for that judgment (what
evidence to use, the required output format, the independence guardrails) live in
[docs/ai-validation-prompt.md](docs/ai-validation-prompt.md) — read that first, it's the
canonical rulebook. It's tool-agnostic (works from any AI chat tool); this repo also ships
ready-to-use integrations so you don't have to paste the prompt by hand every time:

| Where | What | Notes |
|---|---|---|
| Claude Code | `/ssis-tier2-review <Package>` (slash command) | Restricted to `Read`+`Write` only — cannot run Bash, search the filesystem, or reach the web, enforced at the tool-permission layer. |
| Claude Code | `ssis-tier2-adjudicate` (Skill) | Same restrictions, auto-invokes on natural language ("review the Tier 2 evidence for Package_Transforms"). |
| Claude Code | `/ssis-generate-evidence <Package>`, `/ssis-validate <Package>` | The mechanical steps (Step 1 above, and running the gates), normal tool access. |
| GitHub Copilot | `/ssis-tier2-review` prompt file | `ask` mode, minimal tools. |
| GitHub Copilot | `ssis-tier2` custom chat mode | No terminal/file-edit tools for the whole session. |
| GitHub Copilot | `/ssis-generate-evidence` prompt file, `copilot-instructions.md` | Mechanical step + always-on repo context. |

See [docs/tier2-tooling-setup.md](docs/tier2-tooling-setup.md) for the full index and exactly
which file does what.

A packet carries two different kinds of question. Most entries, under `## Components`, ask "does
this generated code implement the same *logic* as this SSIS-side evidence?" — a small number,
under `## Correlation gaps`, ask the more basic "which generated code even represents this
component, if any?" (e.g. an OLEDBCommand or FlatFileDestination Gate 3 couldn't correlate by
schema). For those, the packet reuses Gate 1's own SQL-content correlation when it already found
one, or a short, capped list of name-mention candidates from this package's own generated files
when it didn't — never an open-ended search. Either way, the AI's job is judging what's already
in the packet, never going looking for more itself.

The output of a Tier 2 review is always a new `<package>.ai-review.md` file, saved next to its
evidence packet under `ai_context/<PackageName>/` — never merged into or overwriting Gate 1/2/3's
own reports. `ai_context/` already has real, worked examples from actual adjudication runs across
all packages that produced a packet, including a genuine discrepancy (a Lookup's "no-match rows"
tracking silently dropped in the rewrite), a cross-cutting pattern across three unrelated Script
Components, and two resolved correlation gaps (an OLEDBCommand and a FlatFileDestination, both
confirmed matches) — read those `.ai-review.md` files to see what real output looks like.

### The guardrails, in one place

1. **The evidence packet is the only input.** A Tier 2 judgment never opens the `.dtsx`, other
   generated `.cs` files, or — under any circumstances — `gaps/`, `extract/`, `svk/`,
   `coverage-report.md`, or a generated project's own `README.md` (the code-generation tool's own
   self-reporting; using it would defeat the point of an independent validator).
2. **Never fabricate a match.** Evidence the packet doesn't have is `AiInconclusive`, stated
   precisely — never filled in with an assumption.
3. **Required verdict vocabulary**, each with a rationale citing specific packet evidence:
   `AiConfirmedMatch` / `AiSuspectedDiscrepancy` / `AiInconclusive` / `AiNotApplicable`.
4. **Advisory only.** Tier 1's `gate_reports/` are never edited by a Tier 2 run; a human still
   reads and confirms the `.ai-review.md` before it means anything.
5. A request to "just check the real code to be sure" is declined, not honored — if evidence is
   genuinely missing, the fix is extending the evidence-packet builder
   (`ssis_validator/report/ai_context.py`), not a one-off manual peek that breaks the audit trail.

## Project layout

```
ssis_validator/
  dtsx/         Left-side extraction from .dtsx XML (control_flow, data_flow, sql_extraction,
                schema_types, component_schema, semantic_extraction, no_translation_registry)
  codegen/      Right-side extraction from generated .cs (csharp_sql, csharp_graph,
                csharp_schema, csharp_construct_presence, csharp_related_source)
  compare/      gate1_sql.py, gate2_graph.py, gate3_schema.py — deterministic, Tier 1 only
  report/       json_report.py, markdown_report.py (Tier 1), ai_context.py (Tier 2 packet builder)
  cli.py        `validate` and `ai-context` subcommands
tests/          One test module per gate/module, always proving both the real-match and the
                deliberate-mutation (mismatch) direction — never edits an original fixture
docs/           dtsx-xml-notes.md, dtsx-graph-notes.md (empirical notes), ai-validation-prompt.md
                (Tier 2 rulebook), tier2-tooling-setup.md (Claude/Copilot integration index)
gate_reports/   Tier 1 output (per package, per gate, JSON + Markdown)
ai_context/     Tier 2 output (evidence packets + .ai-review.md, per package)
.claude/        Claude Code commands + skill for Tier 2 (see table above)
.github/        Copilot instructions, prompt files, and chat mode for Tier 2 (see table above)
```

## Tests

```bash
.venv\Scripts\python -m pytest tests/ -v
```

Every gate and extractor is proven both on real matches and on deliberately mutated copies of one
side (never the originals) — a "match" verdict is only trusted once "mismatch" has been proven to
work too. All real-sample tests are skipped automatically if the sample `.dtsx`/generated-project
pair isn't present on the machine running them.
