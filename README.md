# SSIS Rewrite Validation Tool

Independent, static validator that checks whether a C# rewrite generated from an SSIS `.dtsx`
package actually reflects what that package does — without trusting the generation tool that
produced the rewrite. See [SSIS-Rewrite-Validation-Plan.md](SSIS-Rewrite-Validation-Plan.md) for
the full design and the independence rule every line of this tool must respect, and
[docs/dtsx-xml-notes.md](docs/dtsx-xml-notes.md) for the empirical XML/C# shapes the extraction
logic is built from.

## Status

**Gate 1 (SQL statement & schema parity) and Gate 2 (control-flow/data-flow graph parity) are
implemented and proven** against all 5 sample packages. Gate 2's data-flow-internal (per-
component, within one Data Flow Task) comparison is intentionally scoped to an `Unverifiable`
presence-only check — see [docs/dtsx-graph-notes.md](docs/dtsx-graph-notes.md) §6 for why a
stronger claim isn't safe yet. Gate 3 (per-component schema parity) is not yet built.

## Setup

```bash
python -m venv .venv
.venv\Scripts\pip install -e ".[dev]"
```

## Usage

```bash
.venv\Scripts\python -m ssis_validator.cli validate --dtsx <path-to.dtsx> --generated <generated-project-dir> --out <report-dir>
```

Writes `<package>.gate1.{json,md}` and `<package>.gate2.{json,md}` to `--out`. Exit code is
non-zero if any Gate 1 finding is `Mismatch`/`LeftOnly`/`RightOnly`/`Unrecognized`, or any Gate 2
finding is `Mismatch`/`LeftOnly`/`RightOnly`.

## Tests

```bash
.venv\Scripts\python -m pytest tests/ -v
```

`tests/test_gate1.py` and `tests/test_gate2.py` prove each gate both on real matches and on
deliberately mutated copies of each side (never the originals) — per the plan's discipline
(§7.3), a "match" verdict is only trusted once "mismatch" has been proven to work too.
