# SSIS Rewrite Validation Tool

Independent, static validator that checks whether a C# rewrite generated from an SSIS `.dtsx`
package actually reflects what that package does — without trusting the generation tool that
produced the rewrite. See [SSIS-Rewrite-Validation-Plan.md](SSIS-Rewrite-Validation-Plan.md) for
the full design and the independence rule every line of this tool must respect, and
[docs/dtsx-xml-notes.md](docs/dtsx-xml-notes.md) for the empirical XML/C# shapes the extraction
logic is built from.

## Status

**All three gates are implemented and proven** against all 5 sample packages:

- **Gate 1** (SQL statement & schema parity).
- **Gate 2** (control-flow/data-flow graph parity). Its data-flow-internal (per-component, within
  one Data Flow Task) comparison is intentionally scoped to an `Unverifiable` presence-only check
  — see [docs/dtsx-graph-notes.md](docs/dtsx-graph-notes.md) §6 for why a stronger claim isn't
  safe yet.
- **Gate 3** (per-component input/output schema parity). Correlates a destination by (schema,
  table) identity (reusing Gate 1's proven match) and a source by its
  `new <Kind>RowSource<RowType>("ComponentName", ...)` construction site; compares column names
  and SSIS-type-to-.NET-type mapping (verified against Microsoft's published mapping, see
  `ssis_validator/dtsx/schema_types.py`). A destination's compared shape is its genuinely-*mapped*
  columns (`inputColumns`), not its full target-table schema — an unmapped column (an identity
  key, a DB-defaulted audit timestamp) is the database's job to fill in, not the rewrite's, and
  comparing against the full schema produced confirmed false positives during development. A
  known, not-yet-handled limitation: a Data Conversion Transformation's default type-suffixed
  column name (e.g. `CustomerID_i4`) reaching a destination is reported as a name mismatch rather
  than recognized as a renamed match — see `ssis_validator/compare/gate3_schema.py` docstring.

## Setup

```bash
python -m venv .venv
.venv\Scripts\pip install -e ".[dev]"
```

## Usage

```bash
.venv\Scripts\python -m ssis_validator.cli validate --dtsx <path-to.dtsx> --generated <generated-project-dir> --out <report-dir>
```

Writes `<package>.gate{1,2,3}.{json,md}` to `--out`. Exit code is non-zero if any Gate 1 finding
is `Mismatch`/`LeftOnly`/`RightOnly`/`Unrecognized`, any Gate 2 finding is
`Mismatch`/`LeftOnly`/`RightOnly`, or any Gate 3 finding is `Mismatch`/`LeftOnly`.

## Tests

```bash
.venv\Scripts\python -m pytest tests/ -v
```

`tests/test_gate1.py`, `tests/test_gate2.py`, and `tests/test_gate3.py` prove each gate both on
real matches and on deliberately mutated copies of each side (never the originals) — per the
plan's discipline (§7.3), a "match" verdict is only trusted once "mismatch" has been proven to
work too.
