# Gate 2 — expression semantics + generated tests

Gate 2 of [`Migration-Validation-Plan.md`](../../../Migration-Validation-Plan.md) §4: turn
every Derived Column expression the extractor harvests (plan §5.5) into an executable,
generated test, so a rewrite's expression logic is measured against real SSIS behaviour
instead of documentation or memory.

Two pieces, in the order they were built:

1. **`Ssis.Runtime.Expressions`** (`src/Ssis.Runtime.Expressions/`, net8.0, no GAC/Windows
   dependency) — a parser + evaluator for the SSIS expression language, covering exactly the
   operators/functions/casts this PoC's expressions and the ground-truth oracle corpus
   exercise. Not the full SSIS grammar guessed from documentation.
2. **`ssisx testgen`** — consumes that library to generate an xUnit test file per package,
   with boundary cases derived from facts already in the spec.

```powershell
ssisx testgen --input ..\..\SSIS --out out
```

Writes `out/testgen/<Package>.ExpressionTests.cs` (one file per package, one class per Derived
Column expression) and `out/testgen/testgen-summary.md` (covered vs. skipped, with a reason
for every skip).

## Why a semantics library, not just generated assertions

Plan §4 names two deliverables, not one: generated tests, and "a small shared expression-
compatibility library, written once and tested once, rather than reinvented per package." The
library is what the generated tests actually run against — without it a generated `[Fact]`
would have to hand-compute its own expected value (guessed, not measured), and 50 packages
would each reinvent SSIS's own NULL-propagation/casting/string-comparison rules independently,
with 50 chances to get them subtly wrong in different ways. The library also survives past
gate 2: the same NULL-propagation and casting rules apply whenever the actual rewrite ports a
Derived Column expression to C#, so this becomes a real shared dependency, not scaffolding.

## Ground truth, not documentation

Every semantic rule below was **measured against the real SSIS 17 expression evaluator**, the
same "ask the runtime, don't guess" discipline as CLAUDE.md trap 12 — extended here from
probing single object-model properties to probing expression *behaviour*.

- **`src/Ssis.Expression.Oracle/`** (net48, Windows-only, lives in `SsisExtractor.ObjectModel.slnx`
  alongside the plan §7.3 object-model oracle, for the same reason: needs the SSIS 17 GAC) —
  evaluates a fixed corpus of expressions via a package `Variable` with
  `EvaluateAsExpression=true` (reading `.Value` triggers real evaluation) and writes the
  result to a TSV file.
- **`tests/Ssis.Runtime.Expressions.Tests/Fixtures/ssis-expression-oracle.tsv`** — the
  captured corpus, 136 expressions, each row `expression / NULL|VALUE|ERROR / value`. This is
  the library's actual spec: `OracleCorpusTests.MatchesOracle` runs every row through
  `Ssis.Runtime.Expressions` and fails if the result disagrees.
- Regenerate with `dotnet run --project src/Ssis.Expression.Oracle -c Release`, diff before
  overwriting the fixture — a changed row means either the corpus grew or (worth
  investigating) this machine's SSIS version evaluates something differently than what was
  originally measured.

**A real measurement bug was caught, not shipped.** An early version of the oracle reused one
`Package` and one variable name across all ~130 evaluations in a single process. That produced
an internally *contradictory* reading — both `"a" < "A"` and `"A" < "a"` came back `True` in
the same run, which cannot both be correct under any consistent ordering. Root-caused by
re-running the pair in total isolation (fresh `Application`+`Package`+uniquely-named variable)
and getting one consistent answer both times, confirming the shared state was leaking stale
evaluator results across iterations, not a real SSIS behaviour. Fixed by giving every
evaluation in the corpus its own `Application`/`Package`/variable, nothing shared — slower, but
the whole point of an oracle is that its answer can be trusted.

## Semantics catalog — the rules a rewrite must not get wrong by assuming C#/.NET defaults

The single rule everything else follows from: **SSIS never implicitly coerces between types.**
`1 + "a"`, `"1" + 1`, `LEN(123)`, and `SUBSTRING(s,"1",2)` are all measured errors, not
auto-converted. Every operator in the evaluator checks both operand types explicitly.

| Area | Measured behaviour | Naive C# rewrite would likely do instead |
|---|---|---|
| NULL + string concat | NULL propagates: `"a" + NULL(DT_WSTR,10)` → NULL | `"a" + null` → `"a"` (or NRE) |
| NULL + comparison | Three-valued: `NULL(DT_I4) == 1` → NULL, not False | `null == 1` → `false` |
| `&&` / `\|\|` with NULL | Kleene three-valued logic: `false && NULL` → False, `true \|\| NULL` → True, `true && NULL` → NULL | C#'s `&&`/`\|\|` don't have a NULL state at all |
| String equality | **Ordinal** (case-sensitive): `"a" == "A"` → False | Often assumed case-insensitive |
| String ordering | **Culture-aware** (lowercase sorts before its own uppercase; base letters compare alphabetically regardless of case) — verified to match .NET 8's `CultureInfo.InvariantCulture` comparer exactly, despite running on a different CLR/globalization stack (ICU) than the SSIS process (Windows NLS) that produced the corpus | Ordinal compare, which disagrees on `"a" < "A"` |
| `(DT_WSTR,n)`/`(DT_STR,n)` cast | Over-length is a **hard error** — never silently truncates | `.Substring(0, n)` or a truncating cast |
| `(DT_I4)` cast from float | Rounds **half-to-even** (`0.5`→0, `1.5`→2, `2.5`→2) | `(int)` truncates; `Math.Round` defaults to the same half-to-even, but many rewrites use `(int)` directly |
| `(DT_I4)` cast from bool | `TRUE` → **-1** (classic VB/COM convention), not 1 | `Convert.ToInt32(true)` → 1 |
| `(DT_I4)` cast from string | No partial parse (`"12abc"` errors); leading sign/whitespace fine (`" 12 "`, `"+12"` succeed) | `int.Parse` behaves similarly, but `TryParse` failures are easy to silently swallow |
| `SUBSTRING` | 1-based; `start` must be ≥1 (0 **and** negative both error); out-of-range clamps to `""`; `length<0` errors | `.Substring` is 0-based and throws on out-of-range instead of clamping |
| `LEFT`/`RIGHT` | Negative count errors; over-length count clamps | — |
| `TOKEN`/`TOKENCOUNT` | Delimiter argument is a **character set**, not a substring (`TOKEN("a;b,c",",;",2)` → `"b"`); consecutive/leading/trailing delimiters produce no empty tokens (`strtok`-style) — **except** an empty input string, which is exactly one token (itself), not zero | `string.Split` keeps empty entries by default and has no character-set-vs-substring ambiguity to get wrong the other way |
| `FINDSTRING` | An empty search string never matches (always 0), unconditionally | Many "contains" APIs treat `""` as matching everywhere |
| `REPLACE` | An empty search string is a no-op (returns input unchanged) | `string.Replace("", x)` throws in .NET |
| Cast to NULL | A NULL of any type casts to NULL of any other type — no length/format check applied even if the target type would otherwise error | — |

## Function/cast coverage

Exactly what the oracle corpus and this PoC's own harvested expressions exercise (see
`Functions.ReturnTypes` and `SsisTypes.Parse` in the library) — not the full SSIS built-in
list. `ISNULL`/`REPLACENULL` are the only functions that ever see a NULL argument; every other
function propagates NULL generically (any NULL argument → NULL result), enforced once in
`Functions.Call` rather than duplicated per function body.

Covered: `UPPER`, `LOWER`, `TRIM`, `LTRIM`, `RTRIM`, `LEN`, `SUBSTRING`, `LEFT`, `RIGHT`,
`REPLACE`, `FINDSTRING`, `TOKEN`, `TOKENCOUNT`, `ISNULL`, `REPLACENULL`, `DATEPART`, `YEAR`,
`MONTH`, `DAY`, `GETUTCDATE`, `GETDATE`. Casts: `DT_WSTR`, `DT_STR`, `DT_I2/I4/I8`, `DT_R4/R8`,
`DT_NUMERIC`, `DT_BOOL`, `DT_DBTIMESTAMP`, `DT_DBDATE`. Add a function/cast only once a real
expression needs it and a corpus case has measured it — the same "measure, don't anticipate"
discipline as everywhere else in this tool. `DATEPART`'s codes beyond `"yy"` (the only one
actually measured) follow T-SQL's own convention by analogy and are explicitly flagged in code
as unverified.

## Known, deliberate divergence: float arithmetic → string formatting

The oracle corpus shows SSIS's `(DT_WSTR,n)` cast of a float produced by **arithmetic**
(not a plain cast, not a literal) sometimes pads to a fixed decimal-place count that varies by
expression shape — `5.0/2` renders as `"2.500000000000"` (12 decimals) while `(DT_WSTR,20)(1 + 2.5)`
renders as `"3.5"` (no padding), and a plain cast like `(DT_R8)"1e3"` renders as `"1000"` with
no padding at all. This looks like SSIS's expression compiler tracking a "scale" through
literals/arithmetic (similar to `DT_NUMERIC`'s own precision/scale) and threading it into the
cast — internal, undocumented behaviour.

**Deliberately not reproduced.** None of this PoC's actual harvested expressions do float
arithmetic before a string cast — the measured surface (CLAUDE.md) is string concat, `UPPER`,
`SUBSTRING`, `GETUTCDATE`, and `DT_WSTR` casts only. `Ssis.Runtime.Expressions` instead uses
.NET's own shortest-round-trip formatting, which matches every corpus row *not* produced by
float arithmetic. The two affected rows (`5.0/2` and `(DT_WSTR,20)(5.0/2)`) are listed by name
in `OracleCorpusTests.KnownDivergences`, with a test (`KnownDivergences_StillEvaluateWithoutCrashing`)
pinning that they still produce *some* value rather than crash. **A Transformation rule that
does float arithmetic before casting to string needs a human-reviewed golden-value test, not a
generated one** — `ssisx testgen` does not attempt to generate boundary cases for such an
expression's exact string output for this reason.

## `ssisx testgen` — what it generates and why

For each Derived Column output column (an expression with a producing component, a target
type, and resolvable upstream inputs via `LineageBuilder`, plan §5.1):

1. **Resolve referenced columns' types** via lineage edges (`Kind == "ExpressionDerived"`)
   into the producing column, mapped from the pipeline's own `dataType` attribute
   (`TestGenCommand.MapPipelineType` — extend before relying on a type not yet mapped).
   A referenced column with an unmapped type, or no resolvable producer, causes that
   expression's tests to be **skipped and reported** (`testgen-summary.md`), never guessed.
2. **Generate cases**, each evaluated against `Ssis.Runtime.Expressions` **at generation
   time** so the expected value is computed, not hand-authored:
   - `Normal` — a representative value per referenced column.
   - `<Column>IsNull` — one column null-ed at a time (measures NULL propagation for real,
     using the exact expression, not an assumption about it).
   - `<Column>IsEmpty` — for string columns, empty vs. NULL (a very common rewrite
     divergence, per plan §4).
   - `ExceedsTargetLength` — every string-typed referenced column made deliberately overlong,
     when the expression's outer cast (parsed from the expression text itself, so it's
     guaranteed to agree with what gets evaluated) is `DT_WSTR`/`DT_STR`. **Only emitted if it
     actually errors** at generation time — if the expression's own logic (e.g. a `SUBSTRING`)
     prevents the overlong input from reaching the cast unbounded, the case is silently
     dropped rather than asserted against a guessed outcome.
   - `<Column>ShorterThanSubstringWindow` — when the expression's function inventory
     (already extracted by `ExpressionHarvester`) includes `SUBSTRING`.
   - `<Column>CaseFolding` — when it includes `UPPER`/`LOWER` (uses `"straße"`, whose
     invariant-culture uppercase is `"STRAßE"`, not `"STRASSE"` — pins the specific case-
     folding convention rather than assuming full Unicode case mapping).
3. **Exclude non-deterministic expressions.** Any expression calling `GETUTCDATE`/`GETDATE` is
   skipped with a reason, mirroring the non-determinism manifest (plan §5.8) that already
   excludes these columns from golden-dataset comparison at gate 3.

## Measured on this PoC

Running `ssisx testgen` against both packages: **5 expressions covered, 3 skipped** (all three
skips are the `LoadedAtUtc` columns' `GETUTCDATE()` — the exact same three columns the
non-determinism manifest names, see CLAUDE.md's slice-4 note). 30 generated `[Fact]` methods
across the two output files, all compiling and passing when built against a real xUnit project
referencing `Ssis.Runtime.Expressions` — verified end to end, not just eyeballed.

## Honest limits

- **Scope is Derived Column expressions only.** `PropertyExpression`/`ConnectionManagerExpression`/
  `VariableExpression`/`PrecedenceConstraint` kinds (also harvested by `ExpressionHarvester`)
  reference package variables/parameters whose runtime values aren't derivable from the
  extracted spec alone — there's no static input to generate a boundary case from. Not
  attempted here; still visible via `ssisx report`'s `expressions.csv`.
- **Boundary cases are heuristic, not exhaustive.** `ExceedsTargetLength` overloads every
  string-typed input at once rather than isolating which one actually matters; a case that
  doesn't error is dropped rather than proving anything. This finds real divergences (see the
  worked example above) but is not a proof of coverage.
- **This proves the library's/extractor's current UNDERSTANDING, not that a rewrite is
  right.** Same honest-limits framing as gate 1: a generated test passing means the described
  behaviour was captured correctly, not that the eventual C# code satisfies it — that's what
  running the *same* generated assertions against the real rewrite (once one exists) is for.
