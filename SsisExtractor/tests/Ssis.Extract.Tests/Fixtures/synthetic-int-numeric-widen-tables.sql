/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs a small standalone package
    (Fixtures/SyntheticIntNumericWiden.dtsx) added 2026-09-18 -- proves that a
    plain passthrough column whose source buffer is int (DT_I4) but whose
    destination column is decimal (DT_NUMERIC), with no explicit Data
    Conversion component, round-trips exactly. This is the exact real shape
    sql-server-samples' own DailyETLMain.dtsx has (StockHolding_Staging's own
    "Last Cost Price": buffered i4 from a SqlCommand's own `int` result-set
    column, decimal(18,2) at the real destination table).

    UNLIKE every other synthetic-*-coercion-tables.sql fixture in this repo,
    this is NOT a dtexec probe measuring an unknown rounding/truncation rule
    -- int -> decimal is a strictly widening, lossless C#-native implicit
    conversion, so there is nothing to measure. This fixture only proves the
    generated C# round-trips the value exactly (including a negative value),
    the same verification tier every other coercion round in this project's
    history has used, minus the probe step that would be pointless here.

    Both columns are deliberately NOT NULL, unlike most other synthetic
    fixture tables in this repo. A nullable source column here would hit a
    SEPARATE, already-documented, pre-existing limitation unrelated to this
    round's own work -- SqlRowReaderEmitter only guards a column with
    reader.IsDBNull(...) when it is "nullable-inferred" (ISNULL usage in a
    Derived Column, or a Data Conversion's own structural nullability); a
    plain numeric-passthrough-coercion column (NarrowR8ToI4/NarrowI8ToI4/
    .../WidenI4ToNumeric) is never added to that set, so a real NULL would
    throw SqlNullValueException regardless of which coercion pairing is
    involved. CLAUDE.md's own "Numeric-passthrough-coercion for OLE DB/ADO
    NET destinations" section documents this exact same exclusion for the
    original NarrowR8ToI4 probe's own NULL row (ID 10) -- not new here,
    not reproduced here, so this fixture's own generated-code run isn't
    masked by an unrelated failure.

    Run once against .\SQLFORPOC_2022, close to building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-int-numeric-widen-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticIntNumericWidenInput;
GO
CREATE TABLE dbo.SyntheticIntNumericWidenInput
(
    ID  INT NOT NULL,
    Val INT NOT NULL
);
GO
INSERT INTO dbo.SyntheticIntNumericWidenInput (ID, Val) VALUES
    (1, 42),     -- ordinary positive value
    (2, -17),    -- ordinary negative value, confirms the sign survives the widening
    (3, 0);      -- zero
GO

-- ID (not a compound key) deliberately -- matches every other synthetic fixture's own
-- PrimaryKeyInference-friendly naming convention ("<table>ID" or "ID").
DROP TABLE IF EXISTS dbo.SyntheticIntNumericWidenTarget;
GO
CREATE TABLE dbo.SyntheticIntNumericWidenTarget
(
    ID  INT             NOT NULL,
    Val DECIMAL(18, 2)  NOT NULL
);
GO
