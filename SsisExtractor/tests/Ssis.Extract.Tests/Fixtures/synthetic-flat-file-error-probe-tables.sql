/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs Fixtures/SyntheticFlatFileErrorProbe.dtsx
    (Ssis.Extract.FixtureBuilder's "flat-file-error-probe" mode) -- a real dtexec PROBE (not a
    Gate-3 verification fixture) measuring the real ErrorCode values a Flat File Source produces
    for a genuine type-conversion failure (ID column, DT_I4, a non-numeric value) and a genuine
    truncation failure (Name column, DT_WSTR width 5, an overlong value), each with
    ErrorRowDisposition/TruncationRowDisposition = RedirectRow on that column's own OUTPUT
    column. No Script Component is involved here at all -- GetErrorDescription() is a pure
    object-model lookup keyed by error code, measured separately (see the "describe-error"
    probe mode), independent of whether anything actually executes.

    Run once against .\SQLFORPOC_2022, before building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-flat-file-error-probe-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticFlatFileErrorProbeMain;
GO
CREATE TABLE dbo.SyntheticFlatFileErrorProbeMain
(
    ID   INT          NOT NULL,
    Name NVARCHAR(5)  NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticFlatFileErrorProbeErrors;
GO
CREATE TABLE dbo.SyntheticFlatFileErrorProbeErrors
(
    ErrorCode                               INT          NULL,
    ErrorColumn                             INT          NULL,
    -- DT_TEXT (non-Unicode, unbounded large object) -- confirmed via the fixture's own printed
    -- "error output columns" list, not guessed. Needs a non-Unicode destination column (VARCHAR,
    -- not NVARCHAR; a fixed VARCHAR(200) failed OLE DB accessor creation -- 0xC0202025 -- needs
    -- MAX to match DT_TEXT's own unbounded nature).
    [Flat File Source Error Output Column]  VARCHAR(MAX) NULL
);
GO
