/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs a small standalone package
    (Fixtures/SyntheticNumericCoercion.dtsx) built as a real dtexec PROBE,
    added 2026-08-28 -- measures what SSIS's own OLE DB Destination actually
    does when a plain passthrough column's source buffer type (float/DT_R8)
    doesn't match its destination's external metadata type (int/DT_I4), with
    NO explicit Data Conversion component in between. This is the exact real
    shape RBC_Demo_ETL's own DFT_ExcelImport has (CustomerID: r8 from the
    worksheet, i4 at the destination table) -- TransformEmitter currently
    reports this as a generation gap rather than guessing a coercion rule.

    Deliberately excludes an overflow-inducing value (e.g. a float that would
    round past Int32.MaxValue) -- SSIS's own OLE DB Destination typically
    fast-loads in ONE transaction by default (FastLoadMaxInsertCommitSize=0),
    so a single failing row would roll back every other row in this same
    probe, contaminating the rounding-rule measurement below. Overflow
    behavior is measured separately (see synthetic-numeric-coercion-overflow
    files) so a failure there can't take out this probe's own results.

    Run once against .\SQLFORPOC_2022, close to building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-numeric-coercion-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticNumericCoercionInput;
GO
CREATE TABLE dbo.SyntheticNumericCoercionInput
(
    ID  INT   NOT NULL,
    Val FLOAT NULL
);
GO
INSERT INTO dbo.SyntheticNumericCoercionInput (ID, Val) VALUES
    (1, 3.0),   -- exact integer, no rounding needed
    (2, 3.2),   -- rounds down either way (truncate or round-to-nearest)
    (3, 3.5),   -- midpoint -- distinguishes round-half-to-even (-> 4) from truncate (-> 3)
    (4, 3.7),   -- rounds up under round-to-nearest, down under truncate
    (5, -3.2),  -- negative-direction rounds-down case
    (6, -3.5),  -- negative midpoint -- round-half-to-even (-> -4) vs truncate (-> -3)
    (7, -3.7),  -- negative rounds-up-in-magnitude case
    (8, 2.5),   -- another midpoint, even target (2) -- confirms round-half-to-EVEN vs away-from-zero
    (9, 4.5),   -- another midpoint, even target (4) -- same distinction as ID 8 from the other side
    (10, NULL); -- destination column allows NULL -- confirms a NULL passthrough isn't corrupted
GO

-- ID (not a compound key) deliberately -- matches every other synthetic fixture's own
-- PrimaryKeyInference-friendly naming convention ("<table>ID" or "ID").
DROP TABLE IF EXISTS dbo.SyntheticNumericCoercionTarget;
GO
CREATE TABLE dbo.SyntheticNumericCoercionTarget
(
    ID  INT NOT NULL,
    Val INT NULL
);
GO
