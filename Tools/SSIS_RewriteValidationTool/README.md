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

## How this actually works, step by step

This section explains the mechanics in plain language — no code, just what happens to the data as
it flows through the tool.

### 1. Read the SSIS side (`.dtsx` is just XML)

A `.dtsx` file is XML. The tool opens it with Python's built-in XML parser and pulls out, purely
by recognizing known XML shapes (never by guessing):

- **Every SQL statement** the package runs — Execute SQL Task text, a source/destination's query,
  a Lookup's reference query, an OLE DB Command's parameterized statement
  (`ssis_validator/dtsx/sql_extraction.py`).
- **The control-flow graph** — every task/container as a node, every precedence constraint as an
  edge, so the tool can work out "what runs before what" and "what runs in parallel with what"
  from scratch, the same way SSIS itself would (`dtsx/control_flow.py`).
- **The data-flow graph inside each Data Flow Task** — every pipeline component (source,
  transform, destination) as a node, every `<path>` between them as an edge
  (`dtsx/data_flow.py`).
- **Each component's declared column shape** — the column names/types a source produces or a
  destination expects, straight from the XML's `<externalMetadataColumns>` (`dtsx/component_schema.py`).
- **Readable business logic for components with no declared shape** — a Derived Column's or
  Conditional Split's expression is already stored in the XML in a human-readable form
  (`FriendlyExpression`), a Lookup's join key/cache mode/no-match routing are plain attributes, a
  Script Task/Component's actual source code is stored as readable text inside the XML too
  (`dtsx/semantic_extraction.py`).

None of this is guessed from one sample file — every shape here was confirmed by reading Microsoft's
own `.dtsx` documentation and then checking it against all 5 real sample packages before being
trusted (see [docs/dtsx-xml-notes.md](docs/dtsx-xml-notes.md) / [docs/dtsx-graph-notes.md](docs/dtsx-graph-notes.md)).

### 2. Read the generated C# side (regex over real text, not a C# compiler)

The generated project's `.cs` files are read as plain text and matched against a handful of
structural patterns that were confirmed by reading multiple real generated files first — never
invented from assumptions:

- **SQL text embedded in generated code** — the same statement shapes the left side extracts,
  but as they appear in C# string literals (`codegen/csharp_sql.py`).
- **The generated call graph** — every top-level `await SomeTask(...)` call, `Task.WhenAll(...)`
  parallel group, and failure-handler wiring in the package's own orchestration file
  (`codegen/csharp_graph.py`).
- **Declared column classes** — a generated `sealed class` with only auto-properties is recognized
  as one component's declared row shape (`codegen/csharp_schema.py`).
- **"Does this ever appear in the generated project at all?"** — a coarse, whole-project
  presence/absence check used only for a few SSIS component types (Sort, Union All, Merge, Row
  Count) that either get a dedicated wrapper class or don't (`codegen/csharp_construct_presence.py`).
- **Related source hunting** — when a Data Flow Task's own generated method is just wiring
  (`new SomeTransform(...)`), the tool follows the identifiers it references to find the sibling
  method/class file that actually holds the real logic, so later steps aren't stuck looking at a
  thin wrapper (`codegen/csharp_related_source.py`).

### 3. Compare — the three gates

Each gate takes the left-side facts and the right-side facts for the same thing and produces one
**Finding** per item: a verdict (`Match`, `NormalizedMatch`, `Mismatch`, `LeftOnly`, `RightOnly`,
`Unrecognized`, or `Unverifiable`) plus the evidence behind it. Nothing is ever silently skipped.

- **Gate 1 (SQL)** — matches every SQL item by name, then compares the two statements as *parsed
  SQL* (via `sqlglot`), not as raw text, so formatting differences don't create false mismatches.
- **Gate 2 (graph)** — walks the control-flow graph level by level (what must run before what,
  what can run in parallel) and checks the generated call order/`Task.WhenAll` groups match. For
  components living *inside* one Data Flow Task, it only has a name-presence check available (a
  name search isn't proof of anything) — but if Gate 3 already ran a real schema comparison for
  that same component, Gate 2 defers to that stronger answer instead of repeating its own weaker
  one.
- **Gate 3 (per-component schema)** — for every component with a real declared column shape,
  finds its generated counterpart (a destination by matching table identity, a source by its
  generated factory-method name, a flat-file-style destination by an enclosing method named after
  it) and compares column names + SSIS-to-.NET type mapping. A component with nothing to compare
  against (pure pipeline-internal logic, or a confirmed no-counterpart case like Sort/Union
  All/Merge/Row Count) is reported honestly — `Unverifiable` if it's genuinely ambiguous,
  `LeftOnly` if the tool actually proved nothing was generated for it.

### 4. Write the gate reports

Each gate's findings are written twice, in
`gate_reports/<Package>/gates/<Package>.gate{1,2,3}.{json,md}` — one folder per package, and
within it a `gates/` subfolder of its own, so multiple packages' reports never mix together at the
top level and this report type never sits loose alongside `ai_context/`/`gaps/`/`final_review/`:
JSON for anything downstream that needs to read it back (like the final rollup below), Markdown
for a human to open and read directly.

### 5. Where AI actually helps (Tier 2) — and where it doesn't

The gates above never call an AI model. AI only enters at a clearly separate, later, optional
step, and only for the specific slice of findings a structural comparison genuinely cannot settle
— a judgment call like "does this generated code implement the same business logic as this SSIS
expression?" That happens in two mechanical sub-steps plus one judgment step:

1. **Build the evidence packet** (`ai-context` command, no AI, pure extraction) — for every
   surviving `Unverifiable`/`LeftOnly` finding that's a genuine judgment call (not a structural
   gap Tier 1 already resolved), package up exactly what a reviewer needs and nothing else: the
   SSIS-side evidence, the relevant generated code excerpt, and any related source files the tool
   already resolved in step 2 above. Three different shapes of question show up in this packet:
   - **Components** — "does this generated code implement the same logic as this SSIS
     expression/Lookup/Script?"
   - **Correlation gaps** — "we can't even tell which generated code represents this component,
     if any" (e.g. an OLE DB Command or Flat File Destination Gate 3 couldn't correlate by
     schema). The tool gives the AI either a known correlate (when Gate 1 already matched it by
     SQL content) or a short, bounded list of name-mention candidates — never an open-ended
     search.
   - **Left-only gaps** — "Tier 1 actively confirmed nothing was generated for this at all" (a
     missing call site, or one of the Sort/Union All/Merge/Row Count no-counterpart cases). Here
     the AI's job is to double-check Tier 1's own "nothing exists" conclusion, in case the logic
     was actually implemented under a different name or shape.
2. **A human hands that packet to an AI** and asks for a verdict, following the rulebook in
   [docs/ai-validation-prompt.md](docs/ai-validation-prompt.md) — the AI only ever sees the
   packet, never the `.dtsx`, never other generated files, never the code-generation tool's own
   self-reporting. It answers with one of its own verdicts
   (`AiConfirmedMatch`/`AiSuspectedDiscrepancy`/`AiInconclusive`/`AiNotApplicable`, or
   `AiFoundUnmappedImplementation`/`AiConfirmedGap` for a left-only gap), each with a cited
   rationale, saved as `gate_reports/<Package>/ai_context/<Package>.ai-review.md` (+ a structured
   `.ai-review.json` sidecar) — right next to the evidence packet it judged, this is advice for a
   human to check, never a promotion to a Tier 1 verdict, and it never touches that package's own
   `gates/<Package>.gate{1,2,3}.json`/`.md` at all.

### 6. The final rollup

The last, purely mechanical step (`final-summary` command, still no AI call) merges Tier 1's own
gate reports with Tier 2's `.ai-review.json` (if one exists) into one file with four buckets, so a
human only ever has to actually open the last one:

- **Tool-Verified** — Tier 1 alone already proved this is correct.
- **Exception** — Tier 1 already structurally proved there's nothing to check at all (today: an
  SSIS task disabled in the package, so a rewrite correctly generated nothing for it) — kept
  separate from Human-Needs-Review because Tier 1's own per-item conclusion already settled it.
- **AI-Verified** — Tier 1 couldn't resolve it alone, but Tier 2's AI review came back with a
  clean positive.
- **Human-Needs-Review** — everything else: a real Tier 1 problem, an unresolved gap with no Tier
  2 review yet, or a Tier 2 verdict that itself needs a second look.

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

Only needed if you're running the CLI straight from a terminal. The Claude Code/Copilot slash
commands below do this for you automatically the first time, if `.venv` isn't there yet.

## Commands at a glance, and the order to run them

There are exactly three CLI subcommands, always run in this order for one package (steps 2-3 are
optional but recommended). This table is tool-agnostic — the raw command works from any terminal:

| # | Command | Needs AI? | What it produces |
|---|---|---|---|
| 1 | `ssis_validator validate` | No | `gate_reports/<pkg>/gates/<pkg>.gate{1,2,3}.{json,md}` |
| 2 | `ssis_validator ai-context` | No (just packages the evidence) | `gate_reports/<pkg>/ai_context/<pkg>.ai-context.{json,md}` (+ `gate_reports/<pkg>/gaps/*` in non-combined mode) |
| 2b | *(hand the packet to an AI — `/ssis-tier2-review`, the skill, or any chat tool)* | Yes | `gate_reports/<pkg>/ai_context/<pkg>.ai-review.{md,json}` |
| 3 | `ssis_validator final-summary` | No (just merges 1 + 2b) | `gate_reports/<pkg>/final_review/<pkg>.final-summary.{json,md}` |

Step 3 works fine even if you skip steps 2/2b — every unresolved finding just falls to
Human-Needs-Review instead of a chance at AI-Verified. All three commands share one reports-root
directory, `gate_reports` by default (override with `--out`/`--reports-root` on any of them) —
every package gets its own subfolder under it, and each command derives its own named sub-path
from that same root automatically (`validate` → `gates/`, `ai-context` → `ai_context/` and, in
non-combined mode, `gaps/`, `final-summary` → `final_review/`), so you never type the same
directory value three different ways.

### The same sequence, if you're using Claude Code or GitHub Copilot instead of a bare terminal

Every step below is a slash command in **both** tools, with the identical name — only the chat
window differs. Run them in order, once per package:

| # | Step | What you type | Input you need to supply | What happens if you don't |
|---|---|---|---|---|
| 1 | Run Gates 1-3 | `/ssis-validate <PackageName>` | Just the package name — the command searches the workspace for a matching `<PackageName>.dtsx` and a `<PackageName>` generated folder and asks you only if it finds none or more than one match. Give the exact `.dtsx`/generated-folder paths yourself if you already know them. | Nothing to merge in step 3 — Gate 1/3 reports are required for it. |
| 2 | Build the Tier 2 evidence packet | `/ssis-generate-evidence <PackageName>` | Same as step 1 (auto-discovered the same way). | Fine to skip entirely — step 3 still works, everything unresolved just stays Human-Needs-Review. |
| 2b | Get an AI verdict on that packet | `/ssis-tier2-review <PackageName>` (Claude Code also has the `ssis-tier2-adjudicate` skill, which triggers on plain language like "review the Tier 2 evidence for Package_Transforms"; Copilot also has the `ssis-tier2` chat mode) | Nothing extra — it reads the packet step 2 already wrote. | Same as skipping step 2 — nothing to promote out of Human-Needs-Review. |
| 3 | Merge into the final rollup | `/ssis-final-summary <PackageName>` | Just the package name. | You only ever see the raw Gate 1/2/3 + `.ai-review.json` files separately, with no single Tool-Verified/Exception/AI-Verified/Human-Needs-Review view. |

Every one of these four commands **checks for `.venv` and creates it if missing** (`python -m venv
.venv` + `.venv\Scripts\pip install -e ".[dev]"`, i.e. this repo's own Setup section below) before
running anything — you don't have to run Setup by hand first just to use the slash commands. It
still only uses the `.venv` interpreter once it exists, never a bare `python` that might not have
this project installed.

See [docs/tier2-tooling-setup.md](docs/tier2-tooling-setup.md) for the exact file behind each
command and its tool-access restrictions (steps 1/2/3 have normal tool access; step 2b is
deliberately restricted to just reading the packet and writing the review, in both tools).

## Usage — Tier 1 (the deterministic gates)

```bash
.venv\Scripts\python -m ssis_validator.cli validate --dtsx <path-to.dtsx> --generated <generated-project-dir> [--out <reports-root>]
```

Writes `<reports-root>/<package>/gates/<package>.gate{1,2,3}.{json,md}` — `--out` names the *root*
reports directory, not the package's own folder; the CLI derives and creates
`<reports-root>/<package>/gates/` itself. Defaults to `gate_reports` if omitted. Exit code is
non-zero if
any Gate 1 finding is `Mismatch`/`LeftOnly`/`RightOnly`/`Unrecognized`, any Gate 2 finding is
`Mismatch`/`LeftOnly`/`RightOnly`, or any Gate 3 finding is `Mismatch`/`LeftOnly`.

Before rerunning, clean that package's own `gates/` subfolder first —
`<reports-root>/<package>/gates/` — never a sibling package's subfolder or a user-created archive
like `gate_reports/V1/` — so old and new runs for the same package are never mixed.

## Usage — Tier 2 (AI-assisted adjudication)

Tier 2 is two separate steps, deliberately: a mechanical one (no AI, just extraction) and a
judgment one (AI, tightly scoped).

**Step 1 — emit the evidence packet** (deterministic, no AI):

```bash
.venv\Scripts\python -m ssis_validator.cli ai-context --dtsx <path-to.dtsx> --generated <generated-project-dir> --combined
```

Writes into `gate_reports/<package>/ai_context/` by default (`--out` overrides the reports root,
same as `validate` — pass the *same* value to both if you override it, so a package's Tier 1 and
Tier 2 output land under the same folder). `--combined` (recommended) writes one
`<package>.ai-context.md` for the whole package: every
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
| Claude Code | `/ssis-generate-evidence <Package>`, `/ssis-validate <Package>`, `/ssis-final-summary <Package>` | The mechanical steps (Step 1 above, running the gates, and the final rollup), normal tool access. |
| GitHub Copilot | `/ssis-tier2-review` prompt file | `edit` mode — file edits only (so it can save the review itself), no terminal/web. |
| GitHub Copilot | `ssis-tier2` custom chat mode | No terminal/file-edit tools for the whole session. |
| GitHub Copilot | `/ssis-validate`, `/ssis-generate-evidence`, `/ssis-final-summary` prompt files, `copilot-instructions.md` | The mechanical steps (running the gates, Step 1 above, and the final rollup) + always-on repo context. |

See [docs/tier2-tooling-setup.md](docs/tier2-tooling-setup.md) for the full index and exactly
which file does what.

A packet carries three different kinds of question. Most entries, under `## Components`, ask
"does this generated code implement the same *logic* as this SSIS-side evidence?" — a smaller
number, under `## Correlation gaps`, ask the more basic "which generated code even represents this
component, if any?" (e.g. an OLEDBCommand or FlatFileDestination Gate 3 couldn't correlate by
schema); for those, the packet reuses Gate 1's own SQL-content correlation when it already found
one, or a short, capped list of name-mention candidates from this package's own generated files
when it didn't — never an open-ended search. A third, `## Left-only gaps`, is stronger still:
Tier 1 didn't just fail to correlate — it actively *confirmed* nothing was generated at all (a
missing call site, or a Sort/Union All/Merge/Row Count with no counterpart anywhere in the
project); the AI's job there is to double-check that "nothing exists" conclusion, in case the
same behavior was implemented under a different name Tier 1's naming-based checks can't see. In
every case, the AI's job is judging what's already in the packet, never going looking for more
itself.

The output of a Tier 2 review is always a new `<package>.ai-review.md` file, saved next to its
evidence packet under `gate_reports/<PackageName>/ai_context/` — never merged into or overwriting
Gate 1/2/3's own reports. This repo's own `gate_reports/` already has real, worked examples from
actual adjudication runs across all packages that produced a packet, including a genuine
discrepancy (a Lookup's "no-match rows" tracking silently dropped in the rewrite), a cross-cutting
pattern across three unrelated Script Components, and two resolved correlation gaps (an
OLEDBCommand and a FlatFileDestination, both confirmed matches) — read those `.ai-review.md` files
to see what real output looks like.

### The guardrails, in one place

1. **The evidence packet is the only input.** A Tier 2 judgment never opens the `.dtsx`, other
   generated `.cs` files, or — under any circumstances — a `gaps/`, `extract/`, `svk/`,
   `coverage-report.md`, or `README.md` that belongs to the **generated project** (the
   code-generation tool's own self-reporting; using it would defeat the point of an independent
   validator). Unrelated to this validator's own `gate_reports/<package>/gaps/` folder — a packet
   living there is exactly the kind of input this rule is talking about.
2. **Never fabricate a match.** Evidence the packet doesn't have is `AiInconclusive`, stated
   precisely — never filled in with an assumption.
3. **Required verdict vocabulary**, each with a rationale citing specific packet evidence:
   `AiConfirmedMatch` / `AiSuspectedDiscrepancy` / `AiInconclusive` / `AiNotApplicable`.
4. **Advisory only.** Tier 1's `gate_reports/` are never edited by a Tier 2 run; a human still
   reads and confirms the `.ai-review.md` before it means anything.
5. A request to "just check the real code to be sure" is declined, not honored — if evidence is
   genuinely missing, the fix is extending the evidence-packet builder
   (`ssis_validator/report/ai_context.py`), not a one-off manual peek that breaks the audit trail.

## Usage — the final rollup

Once you have `gate_reports/<pkg>/gates/<pkg>.gate{1,3}.json` (from `validate`) and, optionally,
`gate_reports/<pkg>/ai_context/<pkg>.ai-review.json` (from a Tier 2 review), merge them into one
summary:

```bash
.venv\Scripts\python -m ssis_validator.cli final-summary --package <PackageName> [--reports-root <reports-root>]
```

`--reports-root` defaults to `gate_reports` — the same root `validate`/`ai-context` used for this
package, so one value locates both of their outputs plus this package's own subfolder; there's no
separate flag for each directory. Writes `gate_reports/<pkg>/final_review/<pkg>.final-summary.{json,md}`
with the four buckets described in step 6 above (Tool-Verified / Exception / AI-Verified /
Human-Needs-Review). This step is purely mechanical — it only reads JSON already on disk, makes no
AI call, and never re-parses the `.dtsx`/`.cs`. It's safe to run any time, even before Tier 2 has
been run at all (Human-Needs-Review just carries more until it has). In Claude Code or Copilot
Chat this is `/ssis-final-summary <PackageName>` — see the sequencing table above.

## Project layout

```
ssis_validator/
  dtsx/         Left-side extraction from .dtsx XML (control_flow, data_flow, sql_extraction,
                schema_types, component_schema, semantic_extraction, no_translation_registry)
  codegen/      Right-side extraction from generated .cs (csharp_sql, csharp_graph,
                csharp_schema, csharp_construct_presence, csharp_related_source)
  compare/      gate1_sql.py, gate2_graph.py, gate3_schema.py — deterministic, Tier 1 only
  report/       json_report.py, markdown_report.py (Tier 1), ai_context.py (Tier 2 packet
                builder), final_summary.py (merges Tier 1 + Tier 2 into one rollup)
  cli.py        `validate`, `ai-context`, and `final-summary` subcommands
tests/          One test module per gate/module, always proving both the real-match and the
                deliberate-mutation (mismatch) direction — never edits an original fixture
docs/           dtsx-xml-notes.md, dtsx-graph-notes.md (empirical notes), ai-validation-prompt.md
                (Tier 2 rulebook), tier2-tooling-setup.md (Claude/Copilot integration index)
gate_reports/   One subfolder per package, e.g. gate_reports/Package_Advanced/, each report type
                in its own named subfolder:
                  gates/<Package>.gate{1,2,3}.{json,md}      Tier 1 output (validate)
                  ai_context/<Package>.ai-context.{json,md}  Tier 2 evidence packet (ai-context)
                  ai_context/<Package>.ai-review.{md,json}   Tier 2 verdict (a Tier 2 review)
                  gaps/*.correlation-gap.ai-context.*        Standalone gap packets -- only in
                  gaps/*.left-only-gap.ai-context.*          ai-context's non-combined mode
                  final_review/<Package>.final-summary.{json,md}  Tier 1 + Tier 2 rollup (final-summary)
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
