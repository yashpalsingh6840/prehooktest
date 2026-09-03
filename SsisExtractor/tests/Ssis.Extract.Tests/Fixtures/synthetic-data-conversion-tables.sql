/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Back a small standalone package
    (Fixtures/SyntheticDataConversion.dtsx) built to prove Microsoft.DataConvert support end
    to end, added 2026-08-28 -- the gap discovered testing ssisx against a real third-party
    portfolio (SSIS_From_Sandeep)'s Package_Transforms.dtsx (DFT_DerivedAndSplit.DCONV_Types:
    CustomerID/SignupDate, both plain strings, converted to CustomerID_i4 (DT_I4)/
    SignupDate_dt (DT_DBDATE), both dispositions IgnoreFailure).

    This table was FIRST used as an empirical probe -- run through the real SSIS engine (via
    dtexec against the fixture .dtsx, before any extractor/codegen code existed) to observe
    what IgnoreFailure actually does on a genuine conversion/truncation failure, rather than
    guessing from Microsoft's own transformation docs. Deliberately includes: a valid int, a
    valid int padded with whitespace (tests locale-neutral trimming), a non-numeric string, an
    empty string, and a NULL -- and the same shape for the date column. Results of that probe
    run, and what the generator was built to match, are recorded in CLAUDE.md's own "Data
    Conversion component" section.

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-data-conversion-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticDataConversionInput;
GO
CREATE TABLE dbo.SyntheticDataConversionInput
(
    ID              INT           NOT NULL,
    CustomerIdText  NVARCHAR(50)  NULL,
    SignupDateText  NVARCHAR(50)  NULL
);
GO
INSERT INTO dbo.SyntheticDataConversionInput (ID, CustomerIdText, SignupDateText) VALUES
    (1, N'123',        N'2026-01-15'),   -- both valid
    (2, N'  456  ',    N'  2026-02-20  '), -- valid, whitespace-padded (locale-neutral parse)
    (3, N'not-a-number', N'not-a-date'),  -- both invalid -- the actual point of this fixture
    (4, N'',           N''),              -- both empty
    (5, NULL,          NULL);             -- both NULL
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticDataConversionTarget;
GO
CREATE TABLE dbo.SyntheticDataConversionTarget
(
    ID             INT  NOT NULL,
    CustomerId_i4  INT  NULL,
    SignupDate_dt  DATE NULL
);
GO
