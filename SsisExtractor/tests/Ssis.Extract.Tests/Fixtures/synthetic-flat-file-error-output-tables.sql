/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs Fixtures/SyntheticFlatFileErrorOutput.dtsx
    (Ssis.Extract.FixtureBuilder's "flat-file-error-output" mode) -- the real evidenced shape
    (MFDBUpdate.dtsx): Flat File Source main output -> OLE DB Destination (successfully parsed
    rows); error output -> Script Component (ComponentMetaData.GetErrorDescription(Row.ErrorCode)
    + a literal FileName) -> a SECOND OLE DB Destination, 5 columns, all mapped (real client
    evidence's own externalMetadataColumns: ErrorCode/ErrorColumn/FileName str,100; ErrorDescription
    str,1000; ErrorColumnValue text).

    Column types are all non-Unicode (VARCHAR, not NVARCHAR) -- DT_STR/DT_TEXT, not DT_WSTR/
    DT_NTEXT, confirmed via Ssis.Extract.FixtureBuilder's own "flat-file-error-probe" mode
    printing the real error-output column types before this fixture was built.

    Run once against .\SQLFORPOC_2022, before building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-flat-file-error-output-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticFlatFileErrorOutputMain;
GO
CREATE TABLE dbo.SyntheticFlatFileErrorOutputMain
(
    ID   INT          NOT NULL,
    Name NVARCHAR(5)  NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticFlatFileErrorOutputErrors;
GO
CREATE TABLE dbo.SyntheticFlatFileErrorOutputErrors
(
    ErrorCode        VARCHAR(100)  NULL,
    ErrorColumn      VARCHAR(100)  NULL,
    ErrorDescription VARCHAR(1000) NULL,
    ErrorColumnValue VARCHAR(MAX)  NULL,
    FileName         VARCHAR(100)  NULL
);
GO
