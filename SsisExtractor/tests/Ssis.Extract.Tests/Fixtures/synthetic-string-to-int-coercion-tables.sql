/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs a small standalone package
    (Fixtures/SyntheticStringToIntCoercion.dtsx) built as a real dtexec PROBE,
    added 2026-08-30 -- measures what SSIS's own OLE DB Destination actually
    does when a plain passthrough column's source buffer type (string/DT_WSTR)
    doesn't match its destination's external metadata type (int/DT_I4), with
    NO explicit Data Conversion component in between. This is the exact real
    shape RBC_Demo_ETL's own Package_Exports/DFT_AdoNetRoundTrip has
    (CustomerID: wstr,20 from an ADO NET Source reading dbo.StagingCustomers,
    i4 at CustomerExportLog) -- TransformEmitter currently reports this as a
    generation gap rather than guessing a coercion rule. Uses an OLE DB Source
    instead of ADO NET (the real component) because this environment cannot
    construct an ADO.NET connection manager through the SSIS object model
    (documented, unresolved -- see CLAUDE.md's "ADO NET Source/Destination"
    section) -- the coercion itself is source-agnostic, keyed only off the
    buffered type (wstr) vs. the destination's external metadata type (i4),
    the same generic resolution the r8-to-i4 round already established.

    Deliberately excludes an overflow-inducing value (a numeric string beyond
    Int32's range) and any single value expected to hard-fail conversion in
    the SAME batch as the rest -- SSIS's own OLE DB Destination fast-loads in
    ONE transaction by default (FastLoadMaxInsertCommitSize=0), so one failing
    row would roll back every other row and contaminate this probe's own
    measurement of the values that DO succeed. Pathological values (invalid
    text, overflow) are probed separately, one row at a time, so a failure
    there can't take out the main batch's results.

    Run once against .\SQLFORPOC_2022, close to building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-string-to-int-coercion-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticStringToIntCoercionInput;
GO
CREATE TABLE dbo.SyntheticStringToIntCoercionInput
(
    ID  INT           NOT NULL,
    Val NVARCHAR(50)  NULL
);
GO
INSERT INTO dbo.SyntheticStringToIntCoercionInput (ID, Val) VALUES
    (1, N'123'),      -- ordinary numeric string
    (2, N'  456  '),  -- whitespace-padded -- confirms locale-neutral trim, same as every other coercion measured
    (3, N'-42'),      -- negative
    (4, N'007'),      -- leading zeros
    (5, N'0'),        -- zero
    (6, N'+15'),      -- explicit leading plus sign
    (7, NULL);        -- destination column allows NULL -- confirms a NULL passthrough isn't corrupted
GO

-- ID (not a compound key) deliberately -- matches every other synthetic fixture's own
-- PrimaryKeyInference-friendly naming convention ("<table>ID" or "ID").
DROP TABLE IF EXISTS dbo.SyntheticStringToIntCoercionTarget;
GO
CREATE TABLE dbo.SyntheticStringToIntCoercionTarget
(
    ID  INT NOT NULL,
    Val INT NULL
);
GO
