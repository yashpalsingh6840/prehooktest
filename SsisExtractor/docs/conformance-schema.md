# `ssisx conformance` — gate 1 output

Gate 1 of [`Migration-Validation-Plan.md`](../../../Migration-Validation-Plan.md) §3:
turn the extracted spec into the list of obligations a replacement must satisfy, and
report how many are actually accounted for.

**This is the extractor's first output that something else consumes rather than a human
reads.** Every other command (`extract`, `report`, `graph`, `diff`) describes the old
package. This one produces a contract the rewrite is measured against, and an exit code CI
can gate on.

```powershell
# first run: generates rules + an all-Pending claims stub per package
ssisx conformance --input ..\..\SSIS --out out

# later runs: re-reads your edited claims, reports progress
ssisx conformance --input ..\..\SSIS --out out

# in CI: exit 1 unless every obligation is Implemented or explained-NotApplicable
ssisx conformance --input ..\..\SSIS --out out --check
```

## What it writes

```
out/conformance/
  <Package>.rules.json        the generated obligations (regenerated every run, disposable)
  conformance-rules.csv       every rule joined to its claim, one row each
  conformance-report.json     the per-package N/M/K summary
  conformance-report.md       the readable report
  claims/<Package>.claims.json  YOUR file -- hand-maintained, NEVER overwritten
```

**The rules/claims split is the whole design.** Rules are derived from the `.dtsx` and are
thrown away and rebuilt on every run. Claims are human work product accumulated across a
migration — which decisions were made, by whom, and why. `ssisx` writes a claims stub only
when none exists and refuses to touch one that does, because regenerating rules after
someone edits a package must never silently discard the decisions recorded against it.

## Rule categories

One per bullet of Migration-Validation-Plan §3. Every rule carries a `Requirement`
(phrased as an obligation, so it reads as work to do) and `Evidence` (the concrete fact
from the package, so the report is reviewable without opening the `.dtsx` alongside it).

| Category | One rule per | Evidence carries |
|---|---|---|
| `ControlFlow` | executable, at any depth | `ExecutableType`, plus `Disabled` / `MaximumErrorCount` / `TransactionOption` when they aren't the default |
| `Ordering` | precedence constraint | `From -> To`, `Value` (Success/Failure/Completion), `EvalOp`, the constraint expression |
| `DataFlow` | pipeline component | `componentClassID`, and whether the component is asynchronous (blocking) |
| `SourceColumn` | column produced by an input-less component | declared type, error/truncation disposition, and **whether it is read but never consumed** |
| `TargetColumn` | destination column mapping | which pipeline column feeds it, and the target's declared type |
| `TargetSchema` | destination table | the full design-time column contract (`FullName wstr(101)`, `Salary numeric(18,2)`, …) |
| `Transformation` | harvested expression | the friendly expression text, plus the SSIS functions and casts it uses |
| `SqlStatement` | Execute SQL Task / component SQL property | the statement text, collapsed to one line (the full text is already an addressable file under `sql/`) |
| `LoadSemantics` | destination component | `AccessMode`, fast-load options, commit size, error/truncation disposition |
| `ScriptCode` | Script Task / Script Component | language, variables read/written, and whether source was extracted or is stripped |

### Rule ids are a stable contract

Ids look like `TARGET-COLUMN:[dbo].[Employee].FullName` or
`CONTROL-FLOW:Package\DFT_LoadEmployees` — readable rather than hashed, because a human
reads them in a report and types them into a claim file.

**They must stay stable, and that's a correctness requirement, not tidiness.** A claim file
is keyed by rule id and hand-maintained for the length of a migration, so an id that
changed when someone nudged a package in the designer would silently orphan every claim
against it. Ids are therefore derived only from meaning-bearing identifiers (refIds,
object/column names) — never from document order, GUIDs, or counts — and the emitted list
is ordinal-sorted so the file diffs cleanly. `ConformanceTests.RuleIds_AreStableAcrossRepeatedBuilds`
guards all three properties.

One consequence worth knowing when hand-editing a claim file: SSIS refIds contain
backslashes, so rule ids must be JSON-escaped (`"CONTROL-FLOW:Package\\DFT_LoadEmployees"`).
The generated stub already does this correctly — start from it rather than typing ids from
the Markdown report.

## Claim statuses

| Status | Means | Counts toward "accounted for" |
|---|---|---|
| `Implemented` | the replacement does this | yes |
| `NotApplicable` | deliberately not carried over — **`Note` is required** | yes |
| `Pending` | not done yet (the stub's default for every rule) | no |
| *(no claim)* | reported as `Unclaimed` | no |

Two validation rules exist because of how requirements actually get lost:

- **`NotApplicable` without a `Note` is rejected as invalid**, not counted. That
  combination is precisely how a real requirement gets quietly deleted from a migration.
- **An unrecognised `Status` is rejected**, never charitably read as done. A typo like
  `"Done"` fails loudly instead of inflating the number.

`Unclaimed` and `Pending` are counted and reported separately even though both mean "not
done". Pending is known work; Unclaimed usually means the rules were regenerated after the
package changed and nobody re-reviewed the new obligations — the more dangerous of the two.
For the same reason the Markdown report lists **invalid claims** and **unclaimed
obligations** in separate sections: on a package nobody has started, *every* rule is
unclaimed, and folding those together would bury the handful of genuinely malformed claims
that somebody has to fix today.

**Orphaned claims** — a claim naming a rule that no longer exists — are reported rather
than ignored, and fail the gate. The obligation may have moved rather than vanished, so the
right response is re-review, not deletion.

## What this gate does and does not prove

Gate 1 proves nothing is **missing**. It does not prove anything is **correct** — that is
gate 2 (generated expression unit tests) and gate 4 (shadow run + row-hash diff). The plan
is explicit that gate 1's value is catching the defect class that survives code review:
*you forgot this entirely*.

`AccountedForPercent` reaching 100 therefore means every obligation has been consciously
handled, not that the rewrite is right.

**`ScriptCode` rules are flagged `MachineVerifiable: false`** and called out in their own
section of the report. No generated assertion can ever satisfy them — someone has to read
the extracted source and port it deliberately (Migration-Validation-Plan §13, "Script Tasks
remain a manual read"). They are deliberately *not* excluded from any percentage; they're
surfaced so a green number is never mistaken for one.

## Known shape: rules are per task, not per statement

A batched Execute SQL Task produces **one** `SqlStatement` rule whose evidence holds every
statement in the batch. This PoC's own `LoadReferenceData` is the worked example — a single
`SQL_TruncateTargets` task containing `TRUNCATE TABLE dbo.Department; TRUNCATE TABLE
dbo.Designation;`. The task is the addressable unit the SQL harvest already writes to
`sql/`, so keeping rules aligned to it keeps the two outputs cross-referenceable; the cost
is that a reviewer has to read the evidence rather than trust the rule count.
Pinned by `ConformanceTests.LoadReferenceData_CoversBothDataFlowsAndBothTargetTables`.

## Measured on this PoC

| Package | Rules | Notable |
|---|---|---|
| `LoadEmployees` | 31 | 8 target columns, 8 source columns, 6 transformations, 1 ordering constraint |
| `LoadReferenceData` | 49 | two data flows, two target tables, one batched SQL task |

Both are 0% accounted for, correctly — no replacement exists yet. That is the honest
starting state this command is designed to produce, rather than a false green.
