# Work packet: `SCRIPT-COLUMN:SyntheticScriptComponentSeams:SyntheticScriptSeamsTarget.SCR_Cleanse`

- **Package:** `SyntheticScriptComponentSeams`
- **Location:** `SyntheticScriptSeamsTarget.SCR_Cleanse`
- **Tier:** 2 -- missing logic. The real source IS below; port it. Reviewed by a human, then verified by gate 3.
- **Evidence SHA-256:** `d2844e702b62b2bfcf80965720e984f8699410e3dfe5a000876567ce273d6f70`

## Why `ssisx generate` stopped here

Script Component 'SCR_Cleanse' produces 2 column(s) (FullName, IsValid) -- emitted as a single `private partial SCR_CleanseResult SCR_Cleanse(SyntheticScriptSeamsTargetSqlRow row, in RowContext ctx)` seam. The project will not compile (CS8795) until a second part of 'SyntheticScriptSeamsTargetTransform' implements it; apply one with `ssisx apply-fills`.

## What you need to produce

The body of ONE method computing EVERY column this Script Component produces, together, for
a single row -- not one method per column. It will be spliced into the generated transform as
a `partial` method returning the combined record `ssisx generate --seams` already declared, so
it must be a pure function of the row:

```csharp
// ssisx-fill: GapId=<this gap's id> Author=<you> Date=<yyyy-mm-dd> EvidenceSha256=<from the heading above>
private partial <Component>Result <Component>(<RowType> row, in RowContext ctx)
{
    // fields/helpers are allowed here -- this is a class part, not a bare method body.
    return new(<Column1>: ..., <Column2>: ..., ...);
}
```

`<Component>`/`<Component>Result` and the exact record shape (one property per produced
column, with its own C# type) are both already generated -- read them off the seam declaration
this gap's own reason text points at, or the "Columns this component produces" table below.
`<RowType>` is the generated row type, whose properties are the "Input columns available on
`row`" table. Getting every property's type right matters -- the rest of the method signature
is filled in for you when the fill is applied.

The Script Component's full source is below -- port the WHOLE component, computing all of its
columns from the one method (they came from one script; there is exactly one work packet for
it, not one per column).

This method is itself called once PER ROW -- it is exactly where `Input0_ProcessInputRow` ran
in the original. So if the original script called `DateTime.Now`/`DateTime.UtcNow` inside that
per-row callback, calling `DateTime.UtcNow` directly inside THIS method is the faithful port:
it still evaluates fresh per row, only switching local time to UTC (call that timezone change
out; do not assume it is inconsequential). Reach for `ctx.LoadedAtUtc` instead only when the
original value was clearly meant to be ONE shared instant for the whole load -- e.g. it
reproduces an SSIS built-in expression like `GETUTCDATE()`, which this tool's own expression
translator already makes deterministic per load elsewhere, by design. Using `ctx.LoadedAtUtc`
as a substitute for a per-row `DateTime.Now` changes the actual VALUES stored on every row, not
just an implementation detail -- if you choose it anyway, say so explicitly, don't substitute
silently.

## Evidence from the package

- **Data Flow Task:** `DFT_Load`
- **Component:** `SCR_Cleanse`
- **Language:** `CSharp`
- **Reads variables:** _(none)_
- **Writes variables:** _(none)_

### Input columns available on `row`

| Column | SSIS type | Length | C# type |
|---|---|---|---|
| `ID` | `i4` | - | `int` |
| `FirstName` | `wstr` | 50 | `string` |
| `LastName` | `wstr` | 50 | `string` |

### Columns this component produces

The gap above is for this WHOLE component -- return every one of these columns together from the one combined method.

| Column | SSIS type | Length | C# type |
|---|---|---|---|
| `FullName` | `wstr` | 100 | `string` |
| `IsValid` | `bool` | - | `bool` |

### `main.cs`

```csharp
using System;
using Microsoft.SqlServer.Dts.Pipeline;

[SSISScriptComponentEntryPoint]
public class ScriptMain : UserComponent
{
    public override void Input0_ProcessInputRow(Input0Buffer Row)
    {
        // Trim both parts, join with a single space, and drop a trailing space
        // when LastName is empty -- the same shape as the real
        // SCR_CleanseCustomerRow this fixture stands in for.
        Row.FullName = ((Row.FirstName ?? "").Trim() + " " + (Row.LastName ?? "").Trim()).Trim();

        // A row is valid only when BOTH name parts are non-empty after trimming.
        Row.IsValid = (Row.FirstName ?? "").Trim().Length > 0
                   && (Row.LastName ?? "").Trim().Length > 0;
    }
}
```


## SSIS semantics you must preserve

These were measured against the real SSIS evaluator by this project's own oracle, not
assumed. A naive C# port gets several of them wrong:

- **SSIS never implicitly coerces between types.** `1 + "a"`, `"1" + 1` and `LEN(123)` are
  all errors in SSIS, not silent conversions.
- **NULL propagates with SQL-style three-valued logic.** `NULL == 1` is NULL, not `false`.
  `FALSE && NULL` is `false`; `TRUE || NULL` is `true`; `TRUE && NULL` is NULL.
- **String `==`/`!=` are ORDINAL (case-sensitive); `<`/`>`/`<=`/`>=` are CULTURE-AWARE.**
  In C#: `string.Equals(a, b, StringComparison.Ordinal)` versus
  `string.Compare(a, b, StringComparison.InvariantCulture)`. Lowercase sorts before its
  own uppercase.
- **`(DT_WSTR,n)`/`(DT_STR,n)` casts are a HARD ERROR on overflow**, never a silent truncation.
- **`(DT_I4)` on a float rounds half-to-EVEN** (2.5 -> 2, 3.5 -> 4), matching .NET's own
  default `Math.Round`. Casting `TRUE` yields **-1**, not 1.
- **`SUBSTRING` is 1-based and errors when start < 1** (including 0); an out-of-range range
  clamps to `""`.
- **`TOKEN`/`TOKENCOUNT`'s delimiter argument is a CHARACTER SET** (strtok-style), not a
  substring, and an empty input string is exactly one token, not zero.

If the script source uses .NET APIs directly (rather than SSIS expressions), keep its .NET
semantics -- these rules apply to SSIS expression logic being reproduced, not to C# that was
already C#.

## Respond with

A single fenced `csharp` block containing only the code asked for above -- no prose around it,
no `using` directives (the generated file already has them), no explanation inside the block.
Put any caveats AFTER the block, and say plainly if the port is not faithful.

Immediately above the method (or `partial class`) declaration, on its own line, include:

```
// ssisx-fill: GapId=<this gap's id, from the heading above> Author=<your name or email> Date=<yyyy-mm-dd> EvidenceSha256=<the Evidence SHA-256 from the heading above>
```

This is what lets `ssisx apply-fills` tell this port apart from a stale one on a later run,
after the .dtsx has changed underneath it -- without it the fill is still applied (most fills
predate this), just reported as unattributed rather than checked. Copy the id and hash
verbatim from the heading above; do not compute or guess either.